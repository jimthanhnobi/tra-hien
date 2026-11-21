using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    [Fact]
    public async Task ExistingRegularToUniqueRequiresDetailedInventoryAndRejectsTheEntirePatch()
    {
        await using var h = await ProductHarness.Create(fixture);
        var one = await h.Create("One in stock");
        var two = await h.Create("Two in stock");
        var oneVariant = Assert.Single(one.Variants);
        var twoVariant = Assert.Single(two.Variants);
        var opening = Csv((oneVariant.Sku, 1, 100), (twoVariant.Sku, 2, 200));
        Assert.True((await Apply(h.App.Client, opening, await Preview(h.App.Client, opening))).Committed);
        var restricted = await ProductImportTests.CreateStaff(h, "content", true);
        await h.App.LoginAsync(restricted, P009Harness.Password + "-Changed");
        var fingerprint = await Fingerprint(h);
        foreach (var (product, variant) in new[] { (one, oneVariant), (two, twoVariant) })
        {
            using var denied = await h.App.MutateAsync(HttpMethod.Patch, VariantPath(product, variant),
                new { variant.RowVersion, isUniquePiece = true, name = "Must not persist" });
            await Problem(denied, HttpStatusCode.Forbidden, "forbidden");
            var unchanged = await h.Get(product.Id);
            var unchangedVariant = Assert.Single(unchanged.Variants);
            Assert.Equal(product.RowVersion, unchanged.RowVersion);
            Assert.Equal(variant.RowVersion, unchangedVariant.RowVersion);
            Assert.Equal(variant.Name, unchangedVariant.Name);
            Assert.False(unchangedVariant.IsUniquePiece);
        }
        using (var aggregate = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + one.Id,
            new { one.RowVersion, name = "Must not persist", defaultVariant = new { isUniquePiece = true } }))
            await Problem(aggregate, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(one.Name, (await h.Get(one.Id)).Name);
        Assert.Equal(fingerprint, await Fingerprint(h));

        var stillRegular = await h.Change(two, "/variants/" + twoVariant.Id,
            new { isUniquePiece = false, name = "Allowed edit" }, HttpMethod.Patch, twoVariant.RowVersion);
        Assert.Equal("Allowed edit", Assert.Single(stillRegular.Variants).Name);

        var newProduct = await h.Create(initial: new
        {
            name = "New unique product",
            productTypeId = h.Type,
            defaultVariant = new { isUniquePiece = true }
        });
        Assert.True(Assert.Single(newProduct.Variants).IsUniquePiece);
        newProduct = await h.Change(newProduct, "/variants", new { name = "New unique child", isUniquePiece = true });
        Assert.All(newProduct.Variants, v => Assert.True(v.IsUniquePiece));

        // Role composition gives both inventory permissions and catalog.write, with no costs.read.
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        const string editor = "stock-editor@example.test";
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>()
            .Handle(new(editor, "Fixture", P009Harness.Password, ["sales", "content"], h.App.OwnerId, h.App.OwnerStamp), default);
        Assert.True(created.IsSuccess);
        Assert.DoesNotContain("costs.read", created.Value.Permissions);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
            .Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
        await h.App.LoginAsync(editor, P009Harness.Password + "-Changed");
        one = await h.Change(one, "/variants/" + oneVariant.Id, new { isUniquePiece = true, name = "Allowed classification" },
            HttpMethod.Patch, oneVariant.RowVersion);
        oneVariant = Assert.Single(one.Variants);
        Assert.True(oneVariant.IsUniquePiece);
        Assert.Equal("Allowed classification", oneVariant.Name);
        twoVariant = Assert.Single(stillRegular.Variants);
        var beforeConflict = await Fingerprint(h);
        using (var conflict = await h.App.MutateAsync(HttpMethod.Patch, VariantPath(two, twoVariant),
            new { twoVariant.RowVersion, isUniquePiece = true, name = "Must not persist" }))
            await Problem(conflict, HttpStatusCode.Conflict, "unique_piece_violation");
        Assert.Equal(beforeConflict, await Fingerprint(h));
        Assert.Equal("Allowed edit", Assert.Single((await h.Get(two.Id)).Variants).Name);

        await h.App.LoginAsync(restricted, P009Harness.Password + "-Changed");
        one = await h.Change(one, "/variants/" + oneVariant.Id, new { isUniquePiece = true, name = "Existing unique" },
            HttpMethod.Patch, oneVariant.RowVersion);
        oneVariant = Assert.Single(one.Variants);
        Assert.True(oneVariant.IsUniquePiece);
        one = await h.Change(one, "/variants/" + oneVariant.Id, new { isUniquePiece = false }, HttpMethod.Patch, oneVariant.RowVersion);
        Assert.False(Assert.Single(one.Variants).IsUniquePiece);
        Assert.Equal(2L, await Scalar(h, "SELECT count(*) FROM inventory.stock_movements"));
        Assert.Equal(3L, await Scalar(h, "SELECT sum(on_hand) FROM inventory.stock_levels"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSearchRebuildAndOpeningLockProductsInTheSameDatabaseOrder(bool inventoryFirst)
    {
        await using var h = await ProductHarness.Create(fixture);
        var a = await h.Create("First product");
        var b = await h.Create("Second product");
        Guid[] ids;
        await using (var db = h.App.OwnerContext())
            ids = await db.Set<Product>().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync();
        var low = Assert.Single((a.Id == ids[0] ? a : b).Variants);
        var high = Assert.Single((a.Id == ids[1] ? a : b).Variants);
        // Keep heap order opposite to UUID order so an unordered bulk UPDATE cannot pass accidentally.
        await h.App.ExecuteAsync($"UPDATE catalog.products SET search_text=search_text WHERE id='{ids[0]}'; ANALYZE catalog.products");
        Assert.Equal($"{ids[1]},{ids[0]}", await CatalogInventoryFixture.ScalarAsync<string>(h.App.OwnerConnection,
            "SELECT string_agg(id::text, ',' ORDER BY ctid) FROM catalog.products"));

        var file = Csv((high.Sku, 2, 20), (low.Sku, 10, 100000));
        var preview = await Preview(h.App.Client, file);
        var typePath = "/api/v1/admin/product-types/" + h.Type;
        var type = (await h.App.Client.GetFromJsonAsync<ProductTypeView>(typePath))!;
        using (var rename = await h.App.MutateAsync(HttpMethod.Patch, typePath, new { type.RowVersion, name = "Ấm đã đổi tên" }))
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        await h.App.ExecuteAsync("UPDATE platform.outbox_messages SET status='processed', processed_at=now() WHERE type<>'RebuildProductSearchText'");
        var auditBefore = await Scalar(h, "SELECT count(*) FROM platform.audit_logs");
        var outboxBefore = await Scalar(h, "SELECT count(*) FROM platform.outbox_messages");
        var deadlocksBefore = await Scalar(h, "SELECT deadlocks FROM pg_stat_database WHERE datname=current_database()");
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = new NpgsqlConnection(h.App.RuntimeConnection);
        await blocker.OpenAsync(timeout.Token);
        await using var held = await blocker.BeginTransactionAsync(timeout.Token);
        await using var command = new NpgsqlCommand("SELECT id FROM catalog.products WHERE id=@id FOR NO KEY UPDATE", blocker, held);
        command.Parameters.AddWithValue("id", ids[1]);
        await command.ExecuteNonQueryAsync(timeout.Token);

        Task<OpeningResult>? opening = null;
        Task<int>? rebuild = null;
        try
        {
            if (inventoryFirst) opening = Apply(h.App.Client, file, preview);
            else rebuild = processor.ProcessAsync(1, timeout.Token);

            // The first writer must hold the lower row while waiting for the higher row.
            var first = await WaitForProductLockWaiter(h.App.RuntimeConnection, blocker.ProcessID, timeout.Token);
            if (inventoryFirst) rebuild = processor.ProcessAsync(1, timeout.Token);
            else opening = Apply(h.App.Client, file, preview);
            await WaitForProductLockWaiter(h.App.RuntimeConnection, first, timeout.Token);
        }
        finally { await held.RollbackAsync(CancellationToken.None); }

        Assert.NotNull(opening);
        Assert.NotNull(rebuild);
        Assert.True((await opening.WaitAsync(timeout.Token)).Committed);
        Assert.Equal(1, await rebuild.WaitAsync(timeout.Token));
        Assert.Equal(deadlocksBefore, await Scalar(h, "SELECT deadlocks FROM pg_stat_database WHERE datname=current_database()"));
        Assert.Equal(auditBefore + 2, await Scalar(h, "SELECT count(*) FROM platform.audit_logs"));
        Assert.Equal(outboxBefore + 1, await Scalar(h, "SELECT count(*) FROM platform.outbox_messages"));

        await using (var db = h.App.OwnerContext())
        {
            var message = await db.OutboxMessages.SingleAsync(m => m.Type == "RebuildProductSearchText");
            Assert.Equal(("processed", 1, null), (message.Status, message.Attempts, message.LastError));
            Assert.Equal(2, await db.Set<Product>().CountAsync(p => p.SearchText.Contains("am da doi ten")));
            var movements = await db.Set<StockMovement>().ToArrayAsync();
            Assert.Equal(2, movements.Length);
            Assert.Equal(12, movements.Sum(m => m.Quantity));
            foreach (var balance in await db.Set<StockLevel>().ToArrayAsync())
            {
                var movement = Assert.Single(movements, m => m.VariantId == balance.VariantId);
                Assert.Equal(balance.OnHand, movement.OnHandAfter);
                Assert.Equal(balance.VariantId == low.Id ? 100000L : 20L, balance.AvgCostVnd);
            }
        }

        Assert.True((await PostStock(h, low.Id, new("receipt", 10, 130000, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId))).IsSuccess);
        await using var final = h.App.OwnerContext();
        var weighted = await final.Set<StockLevel>().SingleAsync(x => x.VariantId == low.Id);
        Assert.Equal((20, 115000L), (weighted.OnHand, weighted.AvgCostVnd));
        Assert.Equal(20, await final.Set<StockMovement>().Where(x => x.VariantId == low.Id).SumAsync(x => x.Quantity));
    }

    private static string VariantPath(ProductData product, VariantData variant) =>
        ProductApiTests.Root + "/" + product.Id + "/variants/" + variant.Id;

    private static async Task<int> WaitForProductLockWaiter(string connection, int blocker, CancellationToken ct)
    {
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync(ct);
        await using var query = new NpgsqlCommand("""
SELECT pid FROM pg_stat_activity
WHERE datname=current_database() AND wait_event_type='Lock'
  AND @blocker=ANY(pg_blocking_pids(pid))
  AND query LIKE '%FROM catalog.products WHERE id=%ORDER BY id FOR NO KEY UPDATE%'
LIMIT 1
""", observer);
        query.Parameters.AddWithValue("blocker", blocker);
        while (true)
        {
            if (await query.ExecuteScalarAsync(ct) is int pid) return pid;
            await Task.Delay(20, ct);
        }
    }
}
