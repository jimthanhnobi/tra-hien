using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    private const string Receipts = "/api/v1/admin/receipts";
    private const string Stocktakes = "/api/v1/admin/stocktakes";
    private const string Adjustments = Root + "/adjustments";

    [Fact]
    public async Task ReceiptsPostExactlyOnceAndProduceWeightedAverage()
    {
        await using var h = await ProductHarness.Create(fixture);
        var variant = Assert.Single((await h.Create()).Variants);
        var first = await NewReceipt(h, new ReceiptLineInput(variant.Id, 10, 100000));
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
        var version = first.Document.RowVersion;
        var key = Guid.CreateVersion7();
        var path = Receipts + "/" + first.Document.Id + "/post";
        using var posted = await PostReceipt(h, path, version, key);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var body = await posted.Content.ReadAsStringAsync();
        using var replay = await PostReceipt(h, path, version, key);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        using var changedRequest = await PostReceipt(h, path, version + "x", key);
        await Problem(changedRequest, HttpStatusCode.Conflict, "idempotency_key_reused");
        using (var another = await PostReceipt(h, path, version, Guid.CreateVersion7()))
            await Problem(another, HttpStatusCode.Conflict, "concurrency_conflict");
        await h.App.ExecuteAsync("UPDATE platform.idempotency_keys SET created_at=now()-interval '49 hours'");
        using (var expired = await PostReceipt(h, path, version, key))
            await Problem(expired, HttpStatusCode.Conflict, "concurrency_conflict");

        var saved = (await h.App.Client.GetFromJsonAsync<ReceiptView>(Receipts + "/" + first.Document.Id))!;
        Assert.Equal("posted", saved.Document.Status);
        Assert.Equal(h.App.OwnerId, saved.Document.PostedBy);
        using (var cancel = await h.App.MutateAsync(HttpMethod.Post, Receipts + "/" + first.Document.Id + "/cancel",
            new { saved.Document.RowVersion }))
            await Problem(cancel, HttpStatusCode.Conflict, "invalid_status_transition");
        using (var edit = await h.App.MutateAsync(HttpMethod.Patch, Receipts + "/" + first.Document.Id,
            new { saved.Document.RowVersion, note = "Invalid change" }))
            await Problem(edit, HttpStatusCode.Conflict, "invalid_status_transition");

        var second = await NewReceipt(h, new ReceiptLineInput(variant.Id, 10, 130000));
        using var received = await PostReceipt(h, Receipts + "/" + second.Document.Id + "/post", second.Document.RowVersion, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        await using var db = h.App.OwnerContext();
        var stock = await db.Set<StockLevel>().SingleAsync();
        Assert.Equal((20, 115000L), (stock.OnHand, stock.AvgCostVnd));
        var movements = await db.Set<StockMovement>().OrderBy(x => x.OnHandAfter).ToArrayAsync();
        Assert.Equal([10, 20], movements.Select(x => x.OnHandAfter).ToArray());
        Assert.Equal(20, movements.Sum(x => x.Quantity));
        Assert.All(movements, x => Assert.Equal("receipt", x.MovementType));
    }

    [Fact]
    public async Task ReceiptPatchRespectsConcurrencyAndCostPrivacy()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var receipt = await NewReceipt(h, new ReceiptLineInput(v.Id, 1, 123456));
        var uri = Receipts + "/" + receipt.Document.Id;
        var sales = await ProductImportTests.CreateStaff(h, "sales", true);
        var content = await ProductImportTests.CreateStaff(h, "content", true);
        await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        using (var denied = await h.App.Client.GetAsync(uri))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        var hidden = await h.App.Client.GetFromJsonAsync<JsonElement>(uri);
        Assert.False(Assert.Single(hidden.GetProperty("lines").EnumerateArray()).TryGetProperty("unitCostVnd", out _));
        using var patchResponse = await h.App.MutateAsync(HttpMethod.Patch, uri,
            new { receipt.Document.RowVersion, lines = new[] { new { variantId = v.Id, quantity = 2 } } });
        var updated = await ReadStockResponse<ReceiptView>(patchResponse, HttpStatusCode.OK);
        Assert.NotEqual(receipt.Document.RowVersion, updated.Document.RowVersion);
        Assert.Null(Assert.Single(updated.Lines).UnitCostVnd);
        using (var stale = await h.App.MutateAsync(HttpMethod.Patch, uri, new { receipt.Document.RowVersion, note = "stale" }))
            await Problem(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        using (var invalid = await h.App.MutateAsync(HttpMethod.Patch, uri, new { rowVersion = "bad" }))
            await Problem(invalid, HttpStatusCode.BadRequest, "validation_failed");
        using (var duplicate = await h.App.MutateAsync(HttpMethod.Patch, uri,
            new { updated.Document.RowVersion, lines = new[] { new ReceiptLineInput(v.Id, 1, 1), new ReceiptLineInput(v.Id, 1, 1) } }))
            await Problem(duplicate, HttpStatusCode.BadRequest, "validation_failed");

        var page = await h.App.Client.GetFromJsonAsync<JsonElement>(Receipts + "?status=draft&page=1&pageSize=1");
        Assert.Equal(1, page.GetProperty("totalItems").GetInt32());
        Assert.False(Item(page).TryGetProperty("lines", out _));

        await h.App.LoginAsync();
        Assert.Equal(123456, Assert.Single((await h.App.Client.GetFromJsonAsync<ReceiptView>(uri))!.Lines).UnitCostVnd);
        using var cancellation = await h.App.MutateAsync(HttpMethod.Post, uri + "/cancel", new { updated.Document.RowVersion });
        Assert.Equal("cancelled", (await ReadStockResponse<ReceiptView>(cancellation, HttpStatusCode.OK)).Document.Status);
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
        await using var db = h.App.OwnerContext();
        Assert.All(await db.AuditLogs.Where(x => x.EntityType.StartsWith("inventory.receipt")).ToArrayAsync(),
            row => Assert.DoesNotContain("123456", row.Changes, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceivingStaffCanEnterCostButReplayReturnsOnlySafeReceiptMetadata()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var sales = await ProductImportTests.CreateStaff(h, "sales", true);
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        using (var missing = await h.App.MutateAsync(HttpMethod.Post, Receipts, new { lines = new[] { new { variantId = v.Id, quantity = 1 } } }))
            await Problem(missing, HttpStatusCode.BadRequest, "validation_failed");
        var draft = await NewReceipt(h, new ReceiptLineInput(v.Id, 2, 9999));
        Assert.Null(Assert.Single(draft.Lines).UnitCostVnd);
        var path = Receipts + "/" + draft.Document.Id + "/post";
        var key = Guid.CreateVersion7();
        using var initial = await PostReceipt(h, path, draft.Document.RowVersion, key);
        using var replay = await PostReceipt(h, path, draft.Document.RowVersion, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var first = await initial.Content.ReadAsByteArrayAsync();
        Assert.Equal(first, await replay.Content.ReadAsByteArrayAsync());
        using var json = JsonDocument.Parse(first);
        Assert.Equal(["id", "rowVersion", "status"], json.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.NotEqual(draft.Document.RowVersion, json.RootElement.GetProperty("rowVersion").GetString());
        await using var db = h.App.OwnerContext();
        Assert.Equal(9999, (await db.Set<StockLevel>().SingleAsync()).AvgCostVnd);
        Assert.Single(await db.IdempotencyKeys.ToArrayAsync());
    }

    [Fact]
    public async Task ReceiptUniquePieceFailureRollsBackAllOtherLinesAndIdempotency()
    {
        await using var h = await ProductHarness.Create(fixture);
        var unique = Assert.Single((await h.Create(initial: new { name = "Unique inventory", productTypeId = h.Type, defaultVariant = new { isUniquePiece = true } })).Variants);
        var other = Assert.Single((await h.Create("Other product")).Variants);
        using (var opening = await h.App.MutateAsync(HttpMethod.Post, Adjustments, new { variantId = unique.Id, quantityDelta = 1, type = "adjustment", reason = "Initial fixture" }))
            Assert.Equal(HttpStatusCode.OK, opening.StatusCode);
        var receipt = await NewReceipt(h, new ReceiptLineInput(other.Id, 1, 12), new ReceiptLineInput(unique.Id, 1, 12));
        var before = await Fingerprint(h);
        using var response = await PostReceipt(h, Receipts + "/" + receipt.Document.Id + "/post", receipt.Document.RowVersion, Guid.CreateVersion7());
        await Problem(response, HttpStatusCode.Conflict, "unique_piece_violation");
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM platform.idempotency_keys"));
        Assert.Equal("draft", (await h.App.Client.GetFromJsonAsync<ReceiptView>(Receipts + "/" + receipt.Document.Id))!.Document.Status);
    }

    [Fact]
    public async Task StocktakeAppliesDeltaToCurrentBalanceAndRejectsOverlappingCorrection()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var open = Csv((v.Sku, 6, 0));
        Assert.True((await Apply(h.App.Client, open, await Preview(h.App.Client, open))).Committed);
        var a = await NewStocktake(h);
        var b = await NewStocktake(h);
        var code = a.Document.Code;
        Assert.Matches("^KK-[0-9]{6}-[0-9]{3}$", code);
        Assert.NotEqual(code, b.Document.Code);
        a = await Count(h, a, v.Id, 5);
        Assert.Equal(6, Assert.Single(a.Lines).SystemQtyAtCount);
        Assert.Equal(h.App.OwnerId, Assert.Single(a.Lines).CountedBy);
        b = await Count(h, b, v.Id, 5);
        using var damage = await h.App.MutateAsync(HttpMethod.Post, Adjustments,
            new { variantId = v.Id, quantityDelta = -1, type = "damage", reason = "Intervening fixture movement" });
        Assert.Equal(5, (await ReadStockResponse<AdjustmentView>(damage, HttpStatusCode.OK)).OnHandAfter);
        a = await Transition(h, a, "submit");
        b = await Transition(h, b, "submit");
        a = await Transition(h, a, "approve");
        Assert.Equal("approved", a.Document.Status);
        Assert.Equal(h.App.OwnerId, a.Document.ApprovedBy);
        Assert.Equal(4L, await Scalar(h, "SELECT sum(on_hand) FROM inventory.stock_levels"));
        var fingerprint = await Fingerprint(h);
        using var overlapping = await h.App.MutateAsync(HttpMethod.Post, Stocktakes + "/" + b.Document.Id + "/approve", new { b.Document.RowVersion });
        await Problem(overlapping, HttpStatusCode.Conflict, "stocktake_lines_invalid");
        Assert.Contains("stocktake_count_stale", await overlapping.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(fingerprint, await Fingerprint(h));
        b = await Transition(h, b, "cancel");
        Assert.Equal("cancelled", b.Document.Status);

        var zero = await NewStocktake(h);
        zero = await Count(h, zero, v.Id, 4);
        var again = await Count(h, zero, v.Id, 4);
        Assert.NotEqual(zero.Document.RowVersion, again.Document.RowVersion);
        again = await Transition(h, again, "submit");
        again = await Transition(h, again, "approve");
        Assert.Equal("approved", again.Document.Status);
        Assert.Equal(3L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
        using var denied = await h.App.MutateAsync(HttpMethod.Post, Stocktakes + "/" + a.Document.Id + "/cancel", new { a.Document.RowVersion });
        await Problem(denied, HttpStatusCode.Conflict, "invalid_status_transition");
        await using var db = h.App.OwnerContext();
        var correction = await db.Set<StockMovement>().SingleAsync(x => x.MovementType == "stocktake");
        Assert.Equal((-1, 4, 0L, a.Document.Id), (correction.Quantity, correction.OnHandAfter, correction.UnitCostVnd, correction.RefId));
    }

    private static async Task<ReceiptView> NewReceipt(ProductHarness h, params ReceiptLineInput[] lines)
    {
        using var response = await h.App.MutateAsync(HttpMethod.Post, Receipts, new { lines });
        return await ReadStockResponse<ReceiptView>(response, HttpStatusCode.Created);
    }

    private static async Task<HttpResponseMessage> PostReceipt(ProductHarness h, string uri, string rowVersion, Guid key, CancellationToken ct = default)
    {
        var csrf = (await h.App.Client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf", ct))!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { rowVersion }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await h.App.Client.SendAsync(request, ct);
    }

    private static async Task<StocktakeView> NewStocktake(ProductHarness h)
    {
        using var response = await h.App.MutateAsync(HttpMethod.Post, Stocktakes, new { note = "Count fixture" });
        return await ReadStockResponse<StocktakeView>(response, HttpStatusCode.Created);
    }

    private static async Task<StocktakeView> Count(ProductHarness h, StocktakeView view, Guid variant, int countedQty)
    {
        using var response = await h.App.MutateAsync(HttpMethod.Put, Stocktakes + "/" + view.Document.Id + "/lines/" + variant,
            new { view.Document.RowVersion, countedQty });
        return await ReadStockResponse<StocktakeView>(response, HttpStatusCode.OK);
    }

    private static async Task<StocktakeView> Transition(ProductHarness h, StocktakeView view, string action)
    {
        using var response = await h.App.MutateAsync(HttpMethod.Post, Stocktakes + "/" + view.Document.Id + "/" + action,
            new { view.Document.RowVersion });
        return await ReadStockResponse<StocktakeView>(response, HttpStatusCode.OK);
    }

    private static async Task<T> ReadStockResponse<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
