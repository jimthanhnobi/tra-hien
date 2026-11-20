using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CatalogInventoryFixture;

namespace TeaPremium.IntegrationTests;

// P1-04 / NFR-08/09: owner-approved four-column amendment only, no historical inference.
public sealed class TaxonomyMigrationTests(CatalogInventoryFixture fixture) : IClassFixture<CatalogInventoryFixture>
{
    private const string Foundation = "202611190201_P1_01_CatalogInventoryFoundation";
    [Fact]
    public async Task AdditiveMigrationPreservesLegacyDataFksAndNullableHistoryAndIsIdempotent()
    {
        var connection = await fixture.DatabaseAsync(target: Foundation);
        await ExecuteAsync(connection, Seed + """
INSERT INTO catalog.categories(id,slug,name,status) VALUES ('00000000-0000-0000-0000-000000000011','legacy-cat','Legacy category','visible');
INSERT INTO catalog.materials(id,slug,name,kind,sku_code,status) VALUES ('00000000-0000-0000-0000-000000000012','legacy-material','Legacy material','clay','TN','published');
INSERT INTO catalog.artisans(id,slug,name,status) VALUES ('00000000-0000-0000-0000-000000000013','legacy-artisan','Legacy artisan','archived');
INSERT INTO catalog.collections(id,slug,name) VALUES ('00000000-0000-0000-0000-000000000014','legacy-collection','Legacy collection');
UPDATE catalog.products SET primary_category_id='00000000-0000-0000-0000-000000000011', material_id='00000000-0000-0000-0000-000000000012', artisan_id='00000000-0000-0000-0000-000000000013';
INSERT INTO catalog.collection_products(collection_id,product_id,sort_order) VALUES ('00000000-0000-0000-0000-000000000014','00000000-0000-0000-0000-000000000002',7);
""");
        const string fingerprint = """
SELECT md5(string_agg(value::text,E'\n' ORDER BY value::text)) FROM (
 SELECT to_jsonb(t) - 'first_published_at' AS value FROM catalog.categories t UNION ALL
 SELECT to_jsonb(t) - 'first_published_at' FROM catalog.materials t UNION ALL
 SELECT to_jsonb(t) - 'first_published_at' FROM catalog.artisans t UNION ALL
 SELECT to_jsonb(t) - 'first_published_at' FROM catalog.collections t UNION ALL
 SELECT to_jsonb(t) FROM catalog.products t UNION ALL SELECT to_jsonb(t) FROM catalog.variants t UNION ALL
 SELECT to_jsonb(t) FROM catalog.collection_products t UNION ALL SELECT to_jsonb(t) FROM catalog.product_categories t) data
""";
        var before = await ScalarAsync<string>(connection, fingerprint);
        Assert.Equal(0, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema='catalog' AND table_name IN ('categories','collections','materials','artisans') AND column_name='first_published_at'"));
        await using var db = Context(connection);
        var migrator = db.GetService<IMigrator>();
        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations.Keys, x => x.EndsWith("_P1_04_TaxonomyPublicationHistory", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.All(migration.UpOperations, operation =>
        {
            var column = Assert.IsType<AddColumnOperation>(operation);
            Assert.Equal("first_published_at", column.Name); Assert.Equal("timestamp with time zone", column.ColumnType);
            Assert.True(column.IsNullable); Assert.Null(column.DefaultValue); Assert.Null(column.DefaultValueSql);
        });
        var script = migrator.GenerateScript(Foundation, id, MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteAsync(connection, script); await ExecuteAsync(connection, script);
        Assert.Equal(before, await ScalarAsync<string>(connection, fingerprint));
        Assert.Equal(4, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema='catalog' AND table_name IN ('categories','collections','materials','artisans') AND column_name='first_published_at' AND is_nullable='YES' AND column_default IS NULL"));
        Assert.Equal(0, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM (SELECT first_published_at FROM catalog.categories UNION ALL SELECT first_published_at FROM catalog.materials UNION ALL SELECT first_published_at FROM catalog.artisans UNION ALL SELECT first_published_at FROM catalog.collections) x WHERE first_published_at IS NOT NULL"));
        // This legacy fixture proves preservation only: its publication history is intentionally unknown.
        // A real target like this remains HOLD for application rollout until history evidence is approved.
        var runtime = DatabaseProvisioning.Login(connection, "tp_app");
        await ExecuteAsync(runtime, "INSERT INTO catalog.materials(id,name,slug,kind,first_published_at) VALUES(gen_random_uuid(),'New known draft','new-known-draft','wood',NULL)");
        await ErrorAsync(runtime, "ALTER TABLE catalog.materials ADD COLUMN forbidden integer", "42501");
        Assert.False(db.Database.HasPendingModelChanges());
        await migrator.MigrateAsync(Foundation); // disposable, no known publication timestamps to lose
        Assert.Equal(before, await ScalarAsync<string>(connection, fingerprint.Replace("SELECT to_jsonb(t) - 'first_published_at' FROM catalog.materials t", "SELECT to_jsonb(t) - 'first_published_at' FROM catalog.materials t WHERE slug <> 'new-known-draft'", StringComparison.Ordinal)));
        await migrator.MigrateAsync();
    }
    [Fact]
    public async Task DownRefusesToDiscardKnownPublicationHistory()
    {
        var connection = await fixture.DatabaseAsync();
        await ExecuteAsync(connection, "INSERT INTO catalog.materials(id,name,slug,kind,first_published_at) VALUES(gen_random_uuid(),'Known public','known-public','metal','2026-01-01T00:00:00Z')");
        await using var db = Context(connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Foundation));
        Assert.Contains("publication history exists", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM catalog.materials WHERE first_published_at='2026-01-01T00:00:00Z'"));
    }
}
