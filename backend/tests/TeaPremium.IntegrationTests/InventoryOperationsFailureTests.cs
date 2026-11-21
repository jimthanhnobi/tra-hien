using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    [Fact]
    public async Task ConcurrentReceiptPostingAndAllocationAreSerialized()
    {
        var gate = new ParentGate();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(o => o.AddInterceptors(gate)));
        var a = Assert.Single((await h.Create()).Variants);
        var b = Assert.Single((await h.Create("Second lock parent")).Variants);
        var drafts = await Task.WhenAll(NewReceipt(h, new ReceiptLineInput(a.Id, 10, 100), new ReceiptLineInput(b.Id, 1, 5)),
            NewReceipt(h, new ReceiptLineInput(b.Id, 2, 8), new ReceiptLineInput(a.Id, 10, 200)));
        Assert.NotEqual(drafts[0].Document.Code, drafts[1].Document.Code);
        Assert.All(drafts, x => Assert.Matches("^PN-[0-9]{6}-[0-9]{3}$", x.Document.Code));
        var path = Receipts + "/" + drafts[0].Document.Id + "/post";
        var key = Guid.CreateVersion7();
        var otherKey = Guid.CreateVersion7();
        var version = drafts[0].Document.RowVersion;
        gate.Enable();
        var first = PostReceipt(h, path, version, key);
        Task<HttpResponseMessage>? second = null;
        Task<HttpResponseMessage>? replay = null;
        Task<HttpResponseMessage>? different = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = PostReceipt(h, Receipts + "/" + drafts[1].Document.Id + "/post", drafts[1].Document.RowVersion, otherKey);
            replay = PostReceipt(h, path, version, key);
            different = PostReceipt(h, path, version, Guid.CreateVersion7());
            await WaitForParentLock(h.App.RuntimeConnection);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        var results = await Task.WhenAll(first, replay!, different!, second!).WaitAsync(TimeSpan.FromSeconds(25));
        try
        {
            Assert.Equal(HttpStatusCode.OK, results[3].StatusCode);
            Assert.Equal(results[0].StatusCode, results[1].StatusCode);
            Assert.Contains(HttpStatusCode.OK, results.Take(3).Select(x => x.StatusCode));
            Assert.Contains(HttpStatusCode.Conflict, results.Take(3).Select(x => x.StatusCode));
            await using var db = h.App.OwnerContext();
            Assert.Equal(4, await db.Set<StockMovement>().CountAsync());
            Assert.Equal(2, await db.IdempotencyKeys.CountAsync());
            var aStock = await db.Set<StockLevel>().SingleAsync(x => x.VariantId == a.Id);
            Assert.Equal((20, 150L), (aStock.OnHand, aStock.AvgCostVnd));
        }
        finally { foreach (var response in results) response.Dispose(); }
    }

    [Fact]
    public async Task ReceiptCommitFailureRollsBackAllWritesIncludingReplayRecord()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var receipt = await NewReceipt(h, new ReceiptLineInput(v.Id, 2, 12));
        await h.App.ExecuteAsync("""
CREATE FUNCTION platform.test_reject_receipt_commit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.type='StorefrontInvalidate' THEN RAISE EXCEPTION 'test receipt rollback'; END IF;
  RETURN NEW;
END $$;
CREATE CONSTRAINT TRIGGER test_receipt_commit AFTER INSERT ON platform.outbox_messages
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.test_reject_receipt_commit();
""");
        var before = await Fingerprint(h);
        var key = Guid.CreateVersion7();
        var path = Receipts + "/" + receipt.Document.Id + "/post";
        using (var failed = await PostReceipt(h, path, receipt.Document.RowVersion, key))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM platform.idempotency_keys"));
        Assert.Equal("draft", (await h.App.Client.GetFromJsonAsync<ReceiptView>(Receipts + "/" + receipt.Document.Id))!.Document.Status);
        await h.App.ExecuteAsync("DROP TRIGGER test_receipt_commit ON platform.outbox_messages");
        using var retry = await PostReceipt(h, path, receipt.Document.RowVersion, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task ReceiptCancellationAfterFlushDiscardsPostedReceiptAndReplayRecord()
    {
        var stop = new CancelReceiptCommit();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(o => o.AddInterceptors(stop)));
        var v = Assert.Single((await h.Create()).Variants);
        var receipt = await NewReceipt(h, new ReceiptLineInput(v.Id, 2, 25));
        var before = await Fingerprint(h);
        var key = Guid.CreateVersion7();
        var route = Receipts + "/" + receipt.Document.Id + "/post";
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        stop.Cancellation = cancelled;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostReceipt(h, route, receipt.Document.RowVersion, key, cancelled.Token));
        await stop.RolledBack.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("1/1/1/1", stop.PendingWrites);
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM platform.idempotency_keys"));
        using var retry = await PostReceipt(h, route, receipt.Document.RowVersion, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    private sealed class CancelReceiptCommit : DbTransactionInterceptor
    {
        public CancellationTokenSource? Cancellation { get; set; }
        public string? PendingWrites { get; private set; }
        public TaskCompletionSource RolledBack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Cancellation is { } source)
            {
                Cancellation = null;
                await using var cmd = transaction.Connection!.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
SELECT concat_ws('/',
 (SELECT count(*) FROM inventory.stock_levels),
 (SELECT count(*) FROM inventory.stock_movements),
 (SELECT count(*) FROM inventory.receipts WHERE status='posted'),
 (SELECT count(*) FROM platform.idempotency_keys WHERE response_status=200))
""";
                PendingWrites = (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;
                await source.CancelAsync();
                throw new OperationCanceledException(source.Token);
            }
            return result;
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (PendingWrites is not null) RolledBack.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AdjustmentPermissionsAndStockInvariantsUseLockedBalance()
    {
        await using var h = await ProductHarness.Create(fixture);
        var normal = Assert.Single((await h.Create()).Variants);
        var unique = Assert.Single((await h.Create(initial: new { name = "Unique stock", productTypeId = h.Type, defaultVariant = new { isUniquePiece = true } })).Variants);
        var other = Assert.Single((await h.Create("Overflow fixture")).Variants);
        var opening = Csv((unique.Sku, 1, 0), (other.Sku, int.MaxValue, 1));
        Assert.True((await Apply(h.App.Client, opening, await Preview(h.App.Client, opening))).Committed);
        async Task<HttpResponseMessage> Adjust(Guid id, int quantity, string type, string reason) =>
            await h.App.MutateAsync(HttpMethod.Post, Adjustments, new { variantId = id, quantityDelta = quantity, type, reason });
        using (var add = await Adjust(normal.Id, 3, "adjustment", "Found physical items"))
            Assert.Equal(3, (await ReadStockResponse<AdjustmentView>(add, HttpStatusCode.OK)).OnHandAfter);
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET reserved=2 WHERE variant_id='{normal.Id}'");
        var before = await Fingerprint(h);
        using (var over = await Adjust(other.Id, 1, "adjustment", "Valid reason"))
            await Problem(over, HttpStatusCode.Conflict, "stock_quantity_overflow");
        using (var invalid = await Adjust(normal.Id, 0, "adjustment", "Valid reason"))
            await Problem(invalid, HttpStatusCode.BadRequest, "validation_failed");
        using (var reason = await Adjust(normal.Id, -1, "damage", " a "))
            await Problem(reason, HttpStatusCode.BadRequest, "validation_failed");
        using (var direction = await Adjust(normal.Id, 1, "damage", "Invalid direction"))
            await Problem(direction, HttpStatusCode.BadRequest, "validation_failed");
        using (var reserved = await Adjust(normal.Id, -2, "adjustment", "Reserved balance"))
            await Problem(reserved, HttpStatusCode.Conflict, "out_of_stock");
        using (var violation = await Adjust(unique.Id, 1, "adjustment", "Unique violation"))
            await Problem(violation, HttpStatusCode.Conflict, "unique_piece_violation");
        Assert.Equal(before, await Fingerprint(h));

        var sales = await ProductImportTests.CreateStaff(h, "sales", true);
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        var beforeDenied = await Fingerprint(h);
        using (var denied = await Adjust(unique.Id, 1, "adjustment", "Approval required"))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(beforeDenied, await Fingerprint(h));
        using (var damage = await Adjust(normal.Id, -1, "damage", "  Broken piece  "))
            Assert.Equal(2, (await ReadStockResponse<AdjustmentView>(damage, HttpStatusCode.OK)).OnHandAfter);
        using (var zeroCost = await Adjust(unique.Id, -1, "damage", "Zero cost piece"))
            Assert.Equal(0, (await ReadStockResponse<AdjustmentView>(zeroCost, HttpStatusCode.OK)).OnHandAfter);
        await using var db = h.App.OwnerContext();
        var row = await db.Set<StockMovement>().SingleAsync(x => x.VariantId == normal.Id && x.MovementType == "damage");
        Assert.Null(row.UnitCostVnd);
        Assert.Equal("Broken piece", row.Reason);
        Assert.Equal(0L, (await db.Set<StockMovement>().SingleAsync(x => x.VariantId == unique.Id && x.MovementType == "damage")).UnitCostVnd);
    }

    [Fact]
    public async Task StocktakeReportsAllInvalidLinesAndApprovesOnceAfterNewCounts()
    {
        await using var h = await ProductHarness.Create(fixture);
        var ordinary = Assert.Single((await h.Create()).Variants);
        var unique = Assert.Single((await h.Create(initial: new { name = "Unique count", productTypeId = h.Type, defaultVariant = new { isUniquePiece = true } })).Variants);
        var opening = Csv((ordinary.Sku, 2, 30), (unique.Sku, 1, 0));
        Assert.True((await Apply(h.App.Client, opening, await Preview(h.App.Client, opening))).Committed);
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET reserved=1 WHERE variant_id='{ordinary.Id}'");
        var draft = await NewStocktake(h);
        var count = await Count(h, draft, ordinary.Id, 0);
        using (var stale = await h.App.MutateAsync(HttpMethod.Put, Stocktakes + "/" + draft.Document.Id + "/lines/" + unique.Id,
            new { draft.Document.RowVersion, countedQty = 2 }))
            await Problem(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        count = await Count(h, count, unique.Id, 2);
        count = await Transition(h, count, "submit");
        var path = Stocktakes + "/" + count.Document.Id;
        var before = await Fingerprint(h);
        using var rejected = await h.App.MutateAsync(HttpMethod.Post, path + "/approve", new { count.Document.RowVersion });
        await Problem(rejected, HttpStatusCode.Conflict, "stocktake_lines_invalid");
        var errors = (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("out_of_stock", errors.GetProperty($"lines[{ordinary.Id:D}]")[0].GetString());
        Assert.Equal("unique_piece_violation", errors.GetProperty($"lines[{unique.Id:D}]")[0].GetString());
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal("submitted", (await h.App.Client.GetFromJsonAsync<StocktakeView>(path))!.Document.Status);
        await Transition(h, count, "cancel");

        var fresh = await NewStocktake(h);
        fresh = await Count(h, fresh, ordinary.Id, 1);
        fresh = await Count(h, fresh, unique.Id, 0);
        fresh = await Transition(h, fresh, "submit");
        var approvePath = Stocktakes + "/" + fresh.Document.Id + "/approve";
        var results = await Task.WhenAll(h.App.MutateAsync(HttpMethod.Post, approvePath, new { fresh.Document.RowVersion }),
            h.App.MutateAsync(HttpMethod.Post, approvePath, new { fresh.Document.RowVersion }));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in results) response.Dispose(); }
        Assert.Equal(2L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements WHERE movement_type='stocktake'"));
    }

    [Fact]
    public async Task InventoryDocumentRoutesRequireCookieReadyPermissionsAndCsrf()
    {
        await using var h = await ProductHarness.Create(fixture);
        var content = await ProductImportTests.CreateStaff(h, "content", true);
        var unready = await ProductImportTests.CreateStaff(h, "sales", false);
        using var anonymous = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using (var response = await anonymous.GetAsync(Stocktakes))
            await Problem(response, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var response = await h.App.Client.PostAsJsonAsync(Receipts, new { }))
            await Problem(response, HttpStatusCode.BadRequest, "csrf_invalid");
        await h.App.LoginAsync(unready, P009Harness.Password);
        using (var response = await h.App.Client.GetAsync(Receipts))
            await Problem(response, HttpStatusCode.Forbidden, "forbidden");
        await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        using (var response = await h.App.MutateAsync(HttpMethod.Post, Stocktakes, new { note = "Forbidden" }))
            await Problem(response, HttpStatusCode.Forbidden, "forbidden");
        var sales = await ProductImportTests.CreateStaff(h, "sales", true);
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        using (var response = await h.App.MutateAsync(HttpMethod.Post, Stocktakes + "/" + Guid.CreateVersion7() + "/approve", new { rowVersion = "invalid" }))
            await Problem(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.receipts"));
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.stocktakes"));
        Assert.Equal("tp_app", await CatalogInventoryFixture.ScalarAsync<string>(h.App.RuntimeConnection, "SELECT current_user"));
    }
}
