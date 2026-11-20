using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static TeaPremium.IntegrationTests.CatalogInventoryFixture;

namespace TeaPremium.IntegrationTests;

public sealed class CatalogInventoryRollbackTests(CatalogInventoryFixture fixture) : IClassFixture<CatalogInventoryFixture>
{
    [Theory]
    [InlineData("INSERT INTO media.assets(id,storage_key,mime_type,byte_size) VALUES (gen_random_uuid(),'fake','image/png',1)")]
    [InlineData("INSERT INTO media.upload_sessions(id,storage_key,original_name,mime_type,purpose,expected_bytes,expires_at) VALUES (gen_random_uuid(),'fake','fake.png','image/png','catalog',1,now())")]
    [InlineData("INSERT INTO content.redirects(source_path,target_path) VALUES ('/fake-a','/fake-b')")]
    [InlineData("INSERT INTO inventory.receipts(id,code,created_by) VALUES (gen_random_uuid(),'fake',gen_random_uuid())")]
    [InlineData("INSERT INTO inventory.stocktakes(id,code,created_by) VALUES (gen_random_uuid(),'fake',gen_random_uuid())")]
    [InlineData(Seed)]
    [InlineData(Seed + "INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by) SELECT gen_random_uuid(),id,1,now(),now()+interval '1 hour',gen_random_uuid() FROM catalog.variants;")]
    public async Task DownWithDataRefusesBeforeAnyDrop(string seed)
    {
        var c = await fixture.DatabaseAsync(target: "202611190201_P1_01_CatalogInventoryFoundation");
        await ExecuteAsync(c, seed);
        var before = await ShapeAsync(c);
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P0));
        Assert.Contains("P1-01 Down refused: data", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(before, await ShapeAsync(c));
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Theory]
    [InlineData("CREATE TABLE catalog.outside_scope(id int)", "DROP TABLE catalog.outside_scope")]
    [InlineData("CREATE VIEW public.p101_outside AS SELECT id FROM catalog.products", "DROP VIEW public.p101_outside")]
    [InlineData("CREATE FUNCTION public.p101_outside() RETURNS SETOF catalog.products LANGUAGE sql AS 'SELECT * FROM catalog.products'", "DROP FUNCTION public.p101_outside()")]
    [InlineData("CREATE FUNCTION public.p101_outside() RETURNS catalog.products[] LANGUAGE sql AS 'SELECT ARRAY[]::catalog.products[]'", "DROP FUNCTION public.p101_outside()")]
    [InlineData("CREATE TABLE public.p101_outside(id uuid REFERENCES catalog.products(id))", "DROP TABLE public.p101_outside")]
    [InlineData("CREATE INDEX p101_outside ON catalog.products(name)", "DROP INDEX catalog.p101_outside")]
    [InlineData("ALTER TABLE catalog.products ADD COLUMN outside_scope text", "ALTER TABLE catalog.products DROP COLUMN outside_scope")]
    [InlineData("ALTER TABLE catalog.products ADD CONSTRAINT outside_scope CHECK (subtitle IS NULL)", "ALTER TABLE catalog.products DROP CONSTRAINT outside_scope")]
    [InlineData("CREATE FUNCTION content.outside_scope() RETURNS integer LANGUAGE sql AS 'SELECT 1'", "DROP FUNCTION content.outside_scope()")]
    [InlineData("CREATE TYPE catalog.outside_scope AS ENUM ('fake')", "DROP TYPE catalog.outside_scope")]
    [InlineData("CREATE TEXT SEARCH CONFIGURATION catalog.outside_scope (COPY = pg_catalog.simple)", "DROP TEXT SEARCH CONFIGURATION catalog.outside_scope")]
    [InlineData("CREATE TEXT SEARCH DICTIONARY catalog.outside_scope (TEMPLATE = pg_catalog.simple)", "DROP TEXT SEARCH DICTIONARY catalog.outside_scope")]
    [InlineData("CREATE CONVERSION catalog.outside_scope FOR 'UTF8' TO 'LATIN1' FROM pg_catalog.utf8_to_iso8859_1", "DROP CONVERSION catalog.outside_scope")]
    [InlineData("CREATE OPERATOR catalog.=== (LEFTARG = integer, RIGHTARG = integer, FUNCTION = pg_catalog.int4eq)", "DROP OPERATOR catalog.=== (integer, integer)")]
    [InlineData("CREATE STATISTICS catalog.outside_scope ON product_type_id, slug FROM catalog.products", "DROP STATISTICS catalog.outside_scope")]
    [InlineData("CREATE TABLE public.p101_outside(id bigint DEFAULT nextval('catalog.sku_number_seq'))", "DROP TABLE public.p101_outside")]
    public async Task DownWithOutsideObjectOrDependencyRefusesAndThenRecovers(string create, string remove)
    {
        var c = await fixture.DatabaseAsync(target: "202611190201_P1_01_CatalogInventoryFoundation");
        await ExecuteAsync(c, create);
        var before = await ShapeAsync(c);
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P0));
        Assert.Contains("object/dependency outside", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(before, await ShapeAsync(c));
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await ExecuteAsync(c, remove);
        await db.GetService<IMigrator>().MigrateAsync(P0);
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
        await db.Database.MigrateAsync();
        Assert.Equal(25, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
    }

    [Fact]
    public async Task ExternalAdministratorOperatorFamilyBlocksDown()
    {
        var c = await fixture.DatabaseAsync(target: "202611190201_P1_01_CatalogInventoryFoundation");
        var admin = fixture.Admin(c);
        // PostgreSQL requires superuser for CREATE OPERATOR FAMILY. Keep runtime
        // and migration role permissions unchanged; only this disposable fixture
        // administrator creates/removes the external object.
        await ExecuteAsync(admin, "CREATE OPERATOR FAMILY catalog.outside_scope USING btree");
        var shape = await ShapeAsync(c);
        var family = await ScalarAsync<uint>(c, "SELECT oid FROM pg_opfamily WHERE opfnamespace='catalog'::regnamespace AND opfname='outside_scope'");
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P0));
        Assert.Contains("object/dependency outside", error.MessageText, StringComparison.Ordinal);
        Assert.Contains("operator family", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(shape, await ShapeAsync(c));
        Assert.Equal(family, await ScalarAsync<uint>(c, "SELECT oid FROM pg_opfamily WHERE opfnamespace='catalog'::regnamespace AND opfname='outside_scope'"));
        await ExecuteAsync(admin, "DROP OPERATOR FAMILY catalog.outside_scope USING btree");
        await db.GetService<IMigrator>().MigrateAsync(P0);
        await db.Database.MigrateAsync();
        Assert.Equal(25, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
    }

    [Fact]
    public async Task ExternalCollationBlocksDownBeforeAnyDropAndEmptyDownUpStillWorks()
    {
        var c = await fixture.DatabaseAsync(target: "202611190201_P1_01_CatalogInventoryFoundation");
        Assert.StartsWith("17.", await ScalarAsync<string>(c, "SHOW server_version"), StringComparison.Ordinal);
        await ExecuteAsync(c, """
CREATE COLLATION catalog.g4_outside FROM pg_catalog."C";
INSERT INTO platform.settings(key,value) VALUES ('p101.collation_fixture','{}');
SELECT setval('catalog.sku_number_seq',9876);
""");
        var collation = await ScalarAsync<uint>(c, "SELECT 'catalog.g4_outside'::regcollation::oid");
        var shape = await ShapeAsync(c);
        var p0 = await CatalogInventoryDatabaseTests.P0FingerprintAsync(c);
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P0));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("object/dependency outside", error.MessageText, StringComparison.Ordinal);
        Assert.Contains("collation", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(shape, await ShapeAsync(c));
        Assert.Equal(25, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('catalog.sku_number_seq') IS NOT NULL"));
        Assert.Equal(9876L, await ScalarAsync<long>(c, "SELECT last_value FROM catalog.sku_number_seq"));
        Assert.Equal(collation, await ScalarAsync<uint>(c, "SELECT 'catalog.g4_outside'::regcollation::oid"));
        Assert.Equal(p0, await CatalogInventoryDatabaseTests.P0FingerprintAsync(c));
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await ErrorAsync(DatabaseProvisioning.Login(c, "tp_app"), "DELETE FROM platform.audit_logs", "42501");
        await ExecuteAsync(c, "DROP COLLATION catalog.g4_outside");
        await db.GetService<IMigrator>().MigrateAsync(P0);
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
        Assert.Equal(p0, await CatalogInventoryDatabaseTests.P0FingerprintAsync(c));
        await db.Database.MigrateAsync();
        Assert.Equal(25, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content')"));
        Assert.Equal(p0, await CatalogInventoryDatabaseTests.P0FingerprintAsync(c));
    }

    [Fact]
    public async Task EmptyDownUpPreservesP0AndRestrictedPermissions()
    {
        var c = await fixture.DatabaseAsync(target: "202611190201_P1_01_CatalogInventoryFoundation");
        await ExecuteAsync(c, "INSERT INTO platform.settings(key,value) VALUES ('p101.down_fixture','{}')");
        var before = await CatalogInventoryDatabaseTests.P0FingerprintAsync(c);
        await using var db = Context(c);
        await db.GetService<IMigrator>().MigrateAsync(P0);
        Assert.Equal(before, await CatalogInventoryDatabaseTests.P0FingerprintAsync(c));
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('catalog.sku_number_seq') IS NULL"));
        await ErrorAsync(DatabaseProvisioning.Login(c, "tp_app"), "DELETE FROM platform.audit_logs", "42501");
        await db.Database.MigrateAsync();
        Assert.Equal(before, await CatalogInventoryDatabaseTests.P0FingerprintAsync(c));
        await ExecuteAsync(DatabaseProvisioning.Login(c, "tp_app"), Seed);
    }

    private static Task<string> ShapeAsync(string c) => ScalarAsync<string>(c, """
SELECT string_agg(n.nspname||'.'||c.relname||':'||c.oid,E'\n' ORDER BY n.nspname,c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('media','catalog','inventory','pricing','content')
""");
}
