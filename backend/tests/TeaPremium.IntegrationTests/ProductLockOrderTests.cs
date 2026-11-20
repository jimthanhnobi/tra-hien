using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TeaPremium.Application.Catalog;

namespace TeaPremium.IntegrationTests;

// P1-05 / BR-CAT-08/14, NFR-09: observe actual PG locks, not timing-only assertions.
public sealed class ProductLockOrderTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task ProductReferenceFirstMaterialPatchMustWaitBeforeTakingTheSharedGraph()
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        using var create = await app.MutateAsync(HttpMethod.Post, "/api/v1/admin/materials", new { name = "Lock material", kind = "clay", skuCode = "LK" });
        var material = (await create.Content.ReadFromJsonAsync<CatalogView>())!;
        using var publish = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/materials/{material.Id}/publish", new { material.RowVersion });
        var published = (await publish.Content.ReadFromJsonAsync<CatalogPublication>())!;
        await using var product = new NpgsqlConnection(app.RuntimeConnection); await product.OpenAsync();
        await using var tx = await product.BeginTransactionAsync();
        await Execute(product, "LOCK TABLE catalog.product_types, catalog.attribute_definitions, catalog.attribute_options, catalog.materials IN SHARE MODE");
        var patch = app.MutateAsync(HttpMethod.Patch, $"/api/v1/admin/materials/{material.Id}", new { rowVersion = published.RowVersion, name = "Renamed material" });
        try
        {
            await WaitForLock(app.OwnerConnection, "catalog.materials", "RowExclusiveLock");
            // Old P1-04 takes graph -> RowExclusiveLock and fails here. P1-05 must take the table lock first.
            await using var graph = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(8104,100)", product, tx);
            Assert.True((bool)(await graph.ExecuteScalarAsync())!);
        }
        finally { await tx.RollbackAsync(); }
        using var result = await patch.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task MaterialFirstProductReferenceWaitsUntilTheMaterialGraphTransactionCommits()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await using var material = new NpgsqlConnection(app.RuntimeConnection); await material.OpenAsync();
        await using var tx = await material.BeginTransactionAsync();
        await Execute(material, "LOCK TABLE catalog.materials IN ROW EXCLUSIVE MODE; SELECT pg_advisory_xact_lock(8104,100)");
        await using var product = new NpgsqlConnection(app.RuntimeConnection); await product.OpenAsync();
        await using var productTx = await product.BeginTransactionAsync();
        var pending = Execute(product, "LOCK TABLE catalog.product_types, catalog.attribute_definitions, catalog.attribute_options, catalog.materials IN SHARE MODE");
        try { await WaitForLock(app.OwnerConnection, "catalog.materials", "ShareLock"); Assert.False(pending.IsCompleted); }
        finally { await tx.CommitAsync(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        await Execute(product, "SELECT pg_advisory_xact_lock(8104,100)"); await productTx.CommitAsync();
    }

    internal static async Task WaitForLock(string connection, string table, string mode)
    {
        await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_locks WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND relation=to_regclass(@table) AND mode=@mode AND NOT granted)", observer);
            query.Parameters.AddWithValue("table", table); query.Parameters.AddWithValue("mode", mode);
            if ((bool)(await query.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(20, deadline.Token);
        }
    }
    private static async Task Execute(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
}
