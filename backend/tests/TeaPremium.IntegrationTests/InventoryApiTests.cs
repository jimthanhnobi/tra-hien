using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/inventory";
    private const string Header = "X-Inventory-Preview-Token";
    private const string Columns = "sku,quantity,unit_cost_vnd\n";
    private static readonly string[] RestrictedFields = ["lowStock", "lowStockThreshold", "onHand", "reserved", "available", "avgCostVnd", "unitCostVnd"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningPreviewIsReadOnlyAndApplyWritesOneAuditedMovement(bool xlsx)
    {
        await using var h = await ProductHarness.Create(fixture);
        var product = await h.Create();
        var variant = Assert.Single(product.Variants);
        var file = xlsx ? ProductImportTests.Workbook([Columns.TrimEnd().Split(','), [variant.Sku, "10", "0"]],
            sheet: "Tồn đầu kỳ", numericColumn: 1) : Csv((variant.Sku, 10, 0));
        var name = xlsx ? "stock.xlsx" : "stock.csv";
        var baseline = await Fingerprint(h);
        var preview = await Preview(h.App.Client, file, name);
        Assert.Equal(1, preview.ValidRows);
        Assert.False(preview.Committed);
        Assert.Equal(variant.Id, Assert.Single(preview.Rows).VariantId);
        Assert.Equal(baseline, await Fingerprint(h));

        var applied = await Apply(h.App.Client, file, preview, name);
        Assert.True(applied.Committed);
        Assert.Equal(0, applied.InvalidRows);
        await using var db = h.App.OwnerContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var balance = await db.Set<StockLevel>().SingleAsync();
        Assert.Equal((10, 0, 0L), (balance.OnHand, balance.Reserved, balance.AvgCostVnd));
        var movement = await db.Set<StockMovement>().SingleAsync();
        Assert.Equal((variant.Id, "opening", 10, 10, 0L, "staff", h.App.OwnerId, "import"),
            (movement.VariantId, movement.MovementType, movement.Quantity, movement.OnHandAfter, movement.UnitCostVnd, movement.ActorType, movement.ActorId, movement.RefType));
        Assert.NotNull(movement.RefId);
        Assert.Equal(DateTimeKind.Utc, movement.OccurredAt.Kind);
        Assert.Equal(balance.UpdatedAt, movement.OccurredAt);
        var audit = await db.AuditLogs.SingleAsync(x => x.EntityType == "inventory.stock_levels" && x.EntityId == variant.Id);
        Assert.Equal(("staff", h.App.OwnerId, "create"), (audit.ActorType, audit.ActorId, audit.Action));
        using var changes = JsonDocument.Parse(audit.Changes);
        Assert.Equal(JsonValueKind.Null, changes.RootElement.GetProperty("on_hand").GetProperty("before").ValueKind);
        Assert.Equal(10, changes.RootElement.GetProperty("on_hand").GetProperty("after").GetInt32());
        Assert.Equal(0, changes.RootElement.GetProperty("reserved").GetProperty("after").GetInt32());
        Assert.Equal("[redacted]", changes.RootElement.GetProperty("avg_cost_vnd").GetProperty("after").GetString());
        Assert.Equal(1L, await Scalar(h, $"SELECT count(*) FROM platform.outbox_messages WHERE type='StorefrontInvalidate' AND payload->'Tags'=jsonb_build_array('product:{product.Slug}', 'catalog')"));
        Assert.Equal("tp_app", await CatalogInventoryFixture.ScalarAsync<string>(h.App.RuntimeConnection, "SELECT current_user"));
        foreach (var sql in new[] { "UPDATE inventory.stock_movements SET quantity=1", "DELETE FROM inventory.stock_movements", "TRUNCATE inventory.stock_movements" })
            await CatalogInventoryFixture.ErrorAsync(h.App.RuntimeConnection, sql, PostgresErrorCodes.InsufficientPrivilege);

        var committed = await Fingerprint(h);
        var replay = await Apply(h.App.Client, file, preview, name);
        Assert.False(replay.Committed);
        Assert.Equal("opening_already_recorded", Assert.Single(Assert.Single(replay.Rows).Errors).Code);
        Assert.Equal(committed, await Fingerprint(h));
    }

    [Fact]
    public async Task InvalidRowsDuplicatesAndHistoricalZeroBalanceCannotBeApplied()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var old = Assert.Single((await h.Create("Old stock")).Variants);
        await h.App.ExecuteAsync($"""
INSERT INTO inventory.stock_levels(variant_id) VALUES ('{old.Id}');
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,on_hand_after,reason,actor_type,occurred_at) VALUES
  ('{Guid.CreateVersion7()}','{old.Id}','adjustment',1,1,'fixture','system',now()),
  ('{Guid.CreateVersion7()}','{old.Id}','adjustment',-1,0,'fixture','system',now());
""");
        var file = Encoding.UTF8.GetBytes($"\uFEFF{Columns}{v.Sku},1,0\n\n{v.Sku},2,0\n{old.Sku},1,2\nINVALID,2147483648,9223372036854775808\nABC,,\nabc,1,0\n");
        var before = await Fingerprint(h);
        var preview = await Preview(h.App.Client, file);
        Assert.Equal(6, preview.InvalidRows);
        Assert.Null(preview.PreviewToken);
        Assert.Equal([2, 4, 5, 6, 7, 8], preview.Rows.Select(x => x.Row).ToArray());
        Assert.All(preview.Rows.Take(2), x => Assert.Equal("duplicate_sku_in_file", Assert.Single(x.Errors).Code));
        Assert.Equal("opening_already_recorded", Assert.Single(preview.Rows[2].Errors).Code);
        Assert.Equal(["invalid_quantity", "invalid_cost"], preview.Rows[3].Errors.Select(x => x.Code).ToArray());
        Assert.Equal("invalid_sku", Assert.Single(preview.Rows[5].Errors).Code);
        using var rejected = await Send(h.App.Client, file, "false");
        await Problem(rejected, HttpStatusCode.BadRequest, "opening_preview_invalid");
        Assert.Equal(before, await Fingerprint(h));
    }

    [Fact]
    public async Task FileParsingRetainsLimitsAndRejectsInvalidUploads()
    {
        await using var h = await ProductHarness.Create(fixture);
        var sku = Assert.Single((await h.Create()).Variants).Sku;
        var rows = new[] { Columns.TrimEnd().Split(','), new[] { sku, "1", "2" } };
        var before = await Fingerprint(h);
        var cases = new (byte[] Data, string Name, HttpStatusCode Status, string Code)[]
        {
            (Encoding.UTF8.GetBytes("sku,quantity\nA-1,1"), "stock.csv", HttpStatusCode.BadRequest, "missing_header"),
            (Encoding.UTF8.GetBytes(Columns), "stock.csv", HttpStatusCode.BadRequest, "missing_rows"),
            (Encoding.UTF8.GetBytes(Columns + string.Concat(Enumerable.Repeat("A-1,1,2\n", 501))), "stock.csv", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded"),
            (Encoding.Unicode.GetBytes(Columns + sku + ",1,2"), "stock.csv", HttpStatusCode.BadRequest, "invalid_header"),
            (ProductImportTests.Workbook(rows, formula: true), "stock.xlsx", HttpStatusCode.BadRequest, "formula_not_allowed"),
            (ProductImportTests.Workbook(rows, macro: true), "stock.xlsx", HttpStatusCode.UnsupportedMediaType, "unsupported_format"),
            (ProductImportTests.Workbook(rows, expandedOverflow: true), "stock.xlsx", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded"),
            (new byte[5 * 1024 * 1024 + 1], "stock.csv", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded")
        };
        foreach (var item in cases)
        {
            using var response = await Send(h.App.Client, item.Data, "true", filename: item.Name);
            await Problem(response, item.Status, item.Code);
        }
        using var missingMode = await Send(h.App.Client, Csv((sku, 1, 2)), null);
        await Problem(missingMode, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(before, await Fingerprint(h));
    }

    [Fact]
    public async Task PermissionAndFilterChecksProtectActualInventoryAndProductJson()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await h.Create("Ấm tử sa");
        var v = Assert.Single(p.Variants);
        var empty = Assert.Single((await h.Create("Empty")).Variants);
        await h.App.ExecuteAsync($"UPDATE catalog.variants SET low_stock_threshold=2 WHERE id='{v.Id}'");
        var file = Csv((v.Sku, 1, 0));
        var preview = await Preview(h.App.Client, file);
        Assert.True((await Apply(h.App.Client, file, preview)).Committed);

        var moves = $"{Root}/{v.Id}/movements?type=opening&from=2020-01-01&to=2050-01-01&pageSize=1";
        var filtered = await h.App.Client.GetFromJsonAsync<JsonElement>($"{Root}?status=low&type={h.Type}&q=am+tu+sa");
        var ownerStock = Item(filtered);
        Assert.True(ownerStock.GetProperty("lowStock").GetBoolean());
        Assert.Equal(1, ownerStock.GetProperty("onHand").GetInt32());
        Assert.Equal(0L, ownerStock.GetProperty("avgCostVnd").GetInt64());
        Assert.Equal(0L, Item(await h.App.Client.GetFromJsonAsync<JsonElement>(moves)).GetProperty("unitCostVnd").GetInt64());
        Assert.Equal(empty.Id, Item(await h.App.Client.GetFromJsonAsync<JsonElement>(Root + "?status=out")).GetProperty("variantId").GetGuid());
        foreach (var bad in new[] { Root + "?page=0", Root + "?pageSize=101", Root + "?status=foo",
            $"{Root}/{v.Id}/movements?type=foo", $"{Root}/{v.Id}/movements?from=2021-05-07&to=2021-05-06" })
        {
            using var response = await h.App.Client.GetAsync(bad);
            await Problem(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        var content = await ProductImportTests.CreateStaff(h, "content", true);
        var sales = await ProductImportTests.CreateStaff(h, "sales", true);
        var unready = await ProductImportTests.CreateStaff(h, "sales", false);
        using var anonymous = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using var noCookie = await anonymous.GetAsync(Root);
        await Problem(noCookie, HttpStatusCode.Unauthorized, "unauthenticated");
        foreach (var mode in new[] { "true", "false" })
        {
            using var noCookiePost = await Send(anonymous, file, mode, csrf: false);
            await Problem(noCookiePost, HttpStatusCode.Unauthorized, "unauthenticated");
            using var noCsrf = await Send(h.App.Client, file, mode, csrf: false);
            await Problem(noCsrf, HttpStatusCode.BadRequest, "csrf_invalid");
        }

        await h.App.LoginAsync(unready);
        using (var response = await h.App.Client.GetAsync(Root))
            await Problem(response, HttpStatusCode.Forbidden, "forbidden");
        await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        using (var denied = await h.App.Client.GetAsync(Root + "?status=low"))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
        using (var denied = await h.App.Client.GetAsync(ProductApiTests.Root + "?stock=low"))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
        using (var denied = await h.App.Client.GetAsync(moves))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
        using (var denied = await Send(h.App.Client, file, "true"))
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");

        var binary = await h.App.Client.GetFromJsonAsync<JsonElement>(Root + "?status=in&pageSize=1");
        Assert.Equal(1, binary.GetProperty("totalItems").GetInt64());
        var item = Item(binary);
        Assert.Equal("in_stock", item.GetProperty("availability").GetString());
        Assert.All(RestrictedFields, field => Assert.False(item.TryGetProperty(field, out _), field));
        var catalog = await h.App.Client.GetFromJsonAsync<JsonElement>(ProductApiTests.Root + "/" + p.Id);
        var catalogVariant = Assert.Single(catalog.GetProperty("variants").EnumerateArray());
        Assert.All(RestrictedFields, field => Assert.False(catalogVariant.TryGetProperty(field, out _), field));
        var directVariant = await h.App.Client.GetFromJsonAsync<JsonElement>(ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id);
        Assert.False(directVariant.TryGetProperty("lowStockThreshold", out _));
        var listed = await h.App.Client.GetFromJsonAsync<JsonElement>(ProductApiTests.Root + "?q=" + v.Sku);
        Assert.False(Assert.Single(Item(listed).GetProperty("variants").EnumerateArray()).TryGetProperty("lowStockThreshold", out _));

        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        var staffItem = Item(await h.App.Client.GetFromJsonAsync<JsonElement>(Root + "?status=low"));
        Assert.True(staffItem.GetProperty("lowStock").GetBoolean());
        var unrestricted = await h.App.Client.GetFromJsonAsync<JsonElement>(ProductApiTests.Root + "/" + p.Id);
        Assert.Equal(2, Assert.Single(unrestricted.GetProperty("variants").EnumerateArray()).GetProperty("lowStockThreshold").GetInt32());
        Assert.False(staffItem.TryGetProperty("avgCostVnd", out _));
        Assert.False(Item(await h.App.Client.GetFromJsonAsync<JsonElement>(moves)).TryGetProperty("unitCostVnd", out _));
        using var salesApply = await Send(h.App.Client, file, "false", preview.PreviewToken);
        await Problem(salesApply, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task PreviewTokenBindsStaffBytesAndVariantIdentity()
    {
        await using var h = await ProductHarness.Create(fixture);
        var original = await h.Create();
        var v = Assert.Single(original.Variants);
        var other = await h.Create("Second");
        var replacement = Assert.Single(other.Variants);
        var file = Csv((v.Sku, 2, 1));
        var preview = await Preview(h.App.Client, file);
        using (var invalid = await Send(h.App.Client, file, "false", "?invalid?"))
            await Problem(invalid, HttpStatusCode.BadRequest, "opening_preview_invalid");
        using (var changedFile = await Send(h.App.Client, Csv((v.Sku, 2, 2)), "false", preview.PreviewToken))
            await Problem(changedFile, HttpStatusCode.BadRequest, "opening_preview_invalid");
        var anotherOwner = await ProductImportTests.CreateStaff(h, "owner", true);
        await h.App.LoginAsync(anotherOwner, P009Harness.Password + "-Changed");
        using (var wrongActor = await Send(h.App.Client, file, "false", preview.PreviewToken))
            await Problem(wrongActor, HttpStatusCode.BadRequest, "opening_preview_invalid");
        await h.App.LoginAsync();
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.stock_levels"));
        await h.Change(original, "/variants/" + v.Id, new { sku = "REASSIGNED-1" }, HttpMethod.Patch, v.RowVersion);
        await h.Change(other, "/variants/" + replacement.Id, new { sku = v.Sku }, HttpMethod.Patch, replacement.RowVersion);
        var apply = await Apply(h.App.Client, file, preview);
        Assert.False(apply.Committed);
        Assert.Equal("sku_identity_changed", Assert.Single(Assert.Single(apply.Rows).Errors).Code);
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
    }

    [Fact]
    public async Task DatabaseFailureAtCommitRollsBackEveryRowAuditAndOutbox()
    {
        await using var h = await ProductHarness.Create(fixture);
        var first = Assert.Single((await h.Create()).Variants);
        var second = Assert.Single((await h.Create("Second")).Variants);
        var file = Csv((first.Sku, 2, 100), (second.Sku, 3, 200));
        var preview = await Preview(h.App.Client, file);
        await h.App.ExecuteAsync("""
CREATE FUNCTION platform.test_reject_inventory_commit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.type='StorefrontInvalidate' THEN RAISE EXCEPTION 'test inventory rollback'; END IF;
  RETURN NEW;
END $$;
CREATE CONSTRAINT TRIGGER test_inventory_commit AFTER INSERT ON platform.outbox_messages
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.test_reject_inventory_commit();
""");
        var before = await Fingerprint(h);
        using var failed = await Send(h.App.Client, file, "false", preview.PreviewToken);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await Fingerprint(h));
        await h.App.ExecuteAsync("DROP TRIGGER test_inventory_commit ON platform.outbox_messages");
        Assert.True((await Apply(h.App.Client, file, preview)).Committed);
        Assert.Equal(2L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
    }

    [Fact]
    public async Task MutationBoundaryAccumulatesLedgerAndInvalidatesOnlyAvailabilityCrossings()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await h.Create();
        var v = Assert.Single(p.Variants);
        var file = Csv((v.Sku, 10, 100000));
        Assert.True((await Apply(h.App.Client, file, await Preview(h.App.Client, file))).Committed);
        var outbox = await Scalar(h, "SELECT count(*) FROM platform.outbox_messages");
        var receipt = new StockChange("receipt", 10, 130000, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId);
        Assert.True((await PostStock(h, v.Id, receipt)).IsSuccess);
        await using (var db = h.App.OwnerContext())
        {
            var level = await db.Set<StockLevel>().SingleAsync();
            Assert.Equal(115000, level.AvgCostVnd);
            Assert.Equal(20, level.OnHand);
            var movements = await db.Set<StockMovement>().ToArrayAsync();
            Assert.Equal(20, movements.Sum(m => m.Quantity));
            Assert.Equal(20, movements.Single(m => m.MovementType == "receipt").OnHandAfter);
            Assert.Equal(receipt.RefId, movements.Single(m => m.MovementType == "receipt").RefId);
        }
        Assert.Equal(outbox, await Scalar(h, "SELECT count(*) FROM platform.outbox_messages"));
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET reserved=5 WHERE variant_id='{v.Id}'");
        var before = await Fingerprint(h);
        var damage = new StockChange("damage", -20, 115000, "adjustment", null, "fixture damage", "staff", h.App.OwnerId);
        Assert.Equal("out_of_stock", (await PostStock(h, v.Id, damage)).Error.Code);
        Assert.Equal("invalid_stock_movement", (await PostStock(h, v.Id, damage with { Quantity = -1, Reason = "a" })).Error.Code);
        Assert.Equal(before, await Fingerprint(h));
        Assert.True((await PostStock(h, v.Id, damage with { Quantity = -15 })).IsSuccess);
        Assert.Equal(outbox + 1, await Scalar(h, "SELECT count(*) FROM platform.outbox_messages"));
        await using var check = h.App.OwnerContext();
        var remaining = await check.Set<StockLevel>().SingleAsync();
        Assert.Equal((5, 5), (remaining.OnHand, remaining.Reserved));
        Assert.Equal(remaining.OnHand, await check.Set<StockMovement>().SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task UniqueInactiveVariantAcceptsOnePieceAndUnknownCostRemainsDistinctFromZero()
    {
        await using var h = await ProductHarness.Create(fixture);
        var archived = await h.Create("Archived product", new
        {
            name = "Archived product",
            productTypeId = h.Type,
            defaultVariant = new { isUniquePiece = true, status = "inactive" }
        });
        archived = await h.Change(archived, "/archive", new { });
        var unique = Assert.Single(archived.Variants);
        Assert.Equal("inactive", unique.Status);
        var noCost = Assert.Single((await h.Create()).Variants);
        Assert.True((await PostStock(h, noCost.Id,
            new("return_restock", 1, null, "return", Guid.CreateVersion7(), null, "system", null))).IsSuccess);
        var file = Csv((unique.Sku, 1, 0));
        Assert.True((await Apply(h.App.Client, file, await Preview(h.App.Client, file))).Committed);
        var before = await Fingerprint(h);
        Assert.Equal("unique_piece_violation", (await PostStock(h, unique.Id,
            new("receipt", 1, 0L, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId))).Error.Code);
        Assert.Equal(before, await Fingerprint(h));

        await using var db = h.App.OwnerContext();
        Assert.Null((await db.Set<StockLevel>().SingleAsync(x => x.VariantId == noCost.Id)).AvgCostVnd);
        Assert.Equal(0L, (await db.Set<StockLevel>().SingleAsync(x => x.VariantId == unique.Id)).AvgCostVnd);
        Assert.Null((await db.Set<StockMovement>().SingleAsync(x => x.VariantId == noCost.Id)).UnitCostVnd);
    }

    [Fact]
    public async Task PostingCannotReuseLocksFromAnEarlierTransaction()
    {
        await using var h = await ProductHarness.Create(fixture);
        var variant = Assert.Single((await h.Create()).Variants);
        var before = await Fingerprint(h);
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var stock = services.GetRequiredService<IInventoryStockStore>();
        var plan = await unit.ExecuteAsync(async ct =>
        {
            var position = (await stock.LockAsync([variant.Id], ct))[variant.Id];
            var change = new StockChange("opening", 1, 0L, "import", null, null, "staff", h.App.OwnerId);
            Assert.Null(InventoryMutation.Plan(position, change, out var posting));
            return Result.Success(posting!);
        }, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync(_ =>
        {
            services.GetRequiredService<InventoryMutation>().Stage([plan.Value]);
            return Task.FromResult(Result.Success(true));
        }, default));
        Assert.Equal(before, await Fingerprint(h));
    }

    private static async Task<Result<bool>> PostStock(ProductHarness h, Guid id, StockChange change)
    {
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async ct =>
        {
            var states = await scope.ServiceProvider.GetRequiredService<IInventoryStockStore>().LockAsync([id], ct);
            var error = InventoryMutation.Plan(states[id], change, out var posting);
            if (error is not null) return Result.Failure<bool>(Error.BusinessRule(error, error));
            scope.ServiceProvider.GetRequiredService<InventoryMutation>().Stage([posting!]);
            return Result.Success(true);
        }, default);
    }

    private static JsonElement Item(JsonElement page) => Assert.Single(page.GetProperty("items").EnumerateArray());

    private static byte[] Csv(params (string Sku, int Quantity, long Cost)[] rows) =>
        Encoding.UTF8.GetBytes(Columns + string.Join('\n', rows.Select(x => FormattableString.Invariant($"{x.Sku},{x.Quantity},{x.Cost}"))));

    private static async Task<OpeningResult> Preview(HttpClient client, byte[] file, string filename = "stock.csv")
    {
        using var response = await Send(client, file, "true", filename: filename);
        return await ReadOpening(response);
    }

    private static async Task<OpeningResult> Apply(HttpClient client, byte[] file, OpeningResult preview, string filename = "stock.csv")
    {
        Assert.NotNull(preview.PreviewToken);
        using var response = await Send(client, file, "false", preview.PreviewToken, filename);
        return await ReadOpening(response);
    }

    private static async Task<OpeningResult> ReadOpening(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OpeningResult>())!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, byte[] data, string? mode, string? previewToken = null,
        string filename = "stock.csv", bool csrf = true)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data), "file", filename);
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "/opening-import" + (mode is null ? "" : "?dryRun=" + mode))
        {
            Content = form
        };
        if (csrf)
        {
            var token = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        if (previewToken is not null) request.Headers.Add(Header, previewToken);
        return await client.SendAsync(request);
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static Task<string> Fingerprint(ProductHarness h) => CatalogInventoryFixture.ScalarAsync<string>(h.App.OwnerConnection, """
SELECT concat_ws('|',
  (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY variant_id)::text,'') FROM inventory.stock_levels s),
  (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY id)::text,'') FROM inventory.stock_movements m),
  (SELECT count(*) FROM platform.audit_logs),
  (SELECT count(*) FROM platform.outbox_messages),
  (SELECT concat_ws(',',last_value,is_called) FROM catalog.sku_number_seq))
""");

    private static Task<long> Scalar(ProductHarness h, string sql) =>
        CatalogInventoryFixture.ScalarAsync<long>(h.App.OwnerConnection, sql);
}
