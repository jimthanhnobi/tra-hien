using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CatalogInventoryFixture;

namespace TeaPremium.IntegrationTests;

// P1-01 A1/A3-A9; all connections are to databases created by this PG17 fixture.
public sealed class CatalogInventoryDatabaseTests(CatalogInventoryFixture fixture) : IClassFixture<CatalogInventoryFixture>
{
    [Fact]
    public async Task MigrationInventoryAndFrozenGrantsMatchAuthoringContract()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);
        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations.Keys, key => key.EndsWith("_P1_01_CatalogInventoryFoundation", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        Assert.Equal(25, migration.UpOperations.OfType<CreateTableOperation>().Count());
        var sequence = Assert.Single(migration.UpOperations.OfType<CreateSequenceOperation>());
        Assert.Equal("catalog", sequence.Schema);
        Assert.Equal("sku_number_seq", sequence.Name);
        Assert.Empty(migration.UpOperations.OfType<AddColumnOperation>());
        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        var frozenPreflight = sql[0].Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("DO $preflight$", frozenPreflight, StringComparison.Ordinal);
        Assert.Contains("WHERE n.nspname IN ('platform','iam','public')", frozenPreflight, StringComparison.Ordinal);
        var frozenGrants = GrantsSql.CatalogAndInventory().Replace("\r\n", "\n", StringComparison.Ordinal)[(GrantsSql.Preflight.Length + 1)..];
        Assert.Contains(frozenGrants, sql[^1].Sql.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.All(migration.UpOperations.OfType<CreateTableOperation>(), table =>
        {
            // Npgsql models xmin as a virtual row-version operation and omits it
            // from emitted DDL. It must never become a user-defined column.
            Assert.DoesNotContain(table.Columns, column => column.Name == "version");
            Assert.All(table.Columns.Where(column => column.Name == "xmin"), column =>
            {
                Assert.True(column.IsRowVersion);
                Assert.Equal("xid", column.ColumnType);
            });
            Assert.True(table.Schema is "media" or "catalog" or "inventory" or "pricing" or "content");
        });
        var script = db.GetService<IMigrator>().GenerateScript(P0);
        Assert.DoesNotMatch(@"\bxmin\s+xid", script);
    }

    [Fact]
    public async Task EmptyAndExistingP0UpgradePreserveDataAndHistory()
    {
        var connection = await fixture.DatabaseAsync(target: P0);
        await ExecuteAsync(connection, """
INSERT INTO platform.settings(key,value) VALUES ('p101.fixture','{"fake":true}');
INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action)
 VALUES ('00000000-0000-0000-0000-000000000099',now(),'system','platform.settings','p101.fixture','create');
INSERT INTO iam.staff_users(id,display_name,is_active,must_change_password,created_at,email,normalized_email,
 email_confirmed,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
 VALUES ('00000000-0000-0000-0000-000000000097','Synthetic staff',true,true,now(),'fake@example.invalid','FAKE@EXAMPLE.INVALID',
 false,false,false,true,0);
INSERT INTO platform.data_protection_keys(friendly_name,xml) VALUES ('p101-synthetic','<fake/>');
INSERT INTO platform.administrative_data_imports(id,dataset_version,release_order,effective_on,operation,manifest,
 manifest_sha256,csv_sha256,plan_sha256,catalog_sha256,receipt,applied_at)
 VALUES ('00000000-0000-0000-0000-000000000098','fixture',1,'2026-01-01','import','{}',
 repeat('a',64),repeat('b',64),repeat('c',64),repeat('d',64),'{}',now());
""");
        // Fingerprint all P0 tables, including seeded IAM claims and empty ledgers.
        var before = await P0FingerprintAsync(connection);
        await using var db = Context(connection);
        var oldHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await P0FingerprintAsync(connection));
        Assert.Equal(4, oldHistory.Length);
        Assert.Equal(oldHistory, (await db.Database.GetAppliedMigrationsAsync()).Take(4));
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(25, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content') AND NOT (schemaname='pricing' AND tablename IN ('vouchers','voucher_redemptions')) AND NOT (schemaname='inventory' AND tablename='reservations')"));
        Assert.Equal(0, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM pg_tables WHERE tablename IN ('product_reviews','review_media')"));
        Assert.StartsWith("17.", await ScalarAsync<string>(connection, "SHOW server_version"), StringComparison.Ordinal);
        // Exercise an independent empty database as well.
        var empty = await fixture.DatabaseAsync();
        Assert.Equal(25, await ScalarAsync<int>(empty, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('media','catalog','inventory','pricing','content') AND NOT (schemaname='pricing' AND tablename IN ('vouchers','voucher_redemptions')) AND NOT (schemaname='inventory' AND tablename='reservations')"));
    }

    internal static async Task<string> P0FingerprintAsync(string connection) => await ScalarAsync<string>(connection, """
SELECT string_agg(name||':'||data,E'\n' ORDER BY name) FROM (
 SELECT n.nspname||'.'||c.relname name,
   (xpath('/table/row/data/text()',query_to_xml(format(
     'SELECT coalesce(jsonb_agg(j ORDER BY j::text),''[]''::jsonb)::text data FROM (SELECT to_jsonb(t) j FROM %I.%I t) q',n.nspname,c.relname),false,false,'')))[1]::text data
 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname IN ('platform','iam') AND c.relkind='r') q
""");

    [Theory]
    [InlineData("catalog.product_attribute_values", "product_id", "ck_pav_one_value")]
    [InlineData("catalog.variant_attribute_values", "variant_id", "ck_vav_one_value")]
    public async Task EavRequiresExactlyOneTypedValue(string table, string key, string constraint)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        var id = key == "product_id" ? ProductId : VariantId;
        var insert = $"INSERT INTO {table}({key},attribute_id,value_text,value_number) VALUES ('{id}','00000000-0000-0000-0000-000000000004',";
        await ErrorAsync(c, insert + "NULL,NULL)", "23514", constraint);
        await ErrorAsync(c, insert + "'text',1)", "23514", constraint);
        await ExecuteAsync(c, insert + "NULL,9999999999.99)");
        Assert.Equal(9999999999.99m, await ScalarAsync<decimal>(c, $"SELECT value_number FROM {table}"));
        await ErrorAsync(c, $"UPDATE {table} SET value_number=10000000000", "22003");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1L, true)]
    [InlineData(10000000000L, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(10000000001L, false)]
    public async Task BigintPriceBoundsAndNullablePrice(long? price, bool allowed)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        var sql = "UPDATE catalog.variants SET price_vnd=" + (price?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        if (allowed) await ExecuteAsync(c, sql);
        else await ErrorAsync(c, sql, "23514", "ck_variants_price");
        await ExecuteAsync(c, "UPDATE catalog.products SET status='active',published_at=now(); UPDATE catalog.variants SET status='inactive',price_vnd=NULL");
        Assert.True(await ScalarAsync<bool>(c, "SELECT price_vnd IS NULL FROM catalog.variants"));
    }

    [Theory]
    [InlineData("UPDATE catalog.products SET description_word_count=-1", "ck_products_word_count")]
    [InlineData("UPDATE catalog.products SET status='active'", "ck_products_published")]
    [InlineData("UPDATE catalog.products SET slug='Bad Slug'", "ck_products_slug")]
    [InlineData("UPDATE catalog.products SET sku_base='AM-XX-123'", "ck_products_sku_base")]
    [InlineData("UPDATE catalog.variants SET sku='bad sku'", "ck_variants_sku")]
    [InlineData("INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved) SELECT id,1,2 FROM catalog.variants", "ck_stock_reserved")]
    [InlineData("INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,on_hand_after,actor_type,occurred_at) SELECT gen_random_uuid(),id,'sale',1,1,'staff',now() FROM catalog.variants", "ck_mv_direction")]
    [InlineData("INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,on_hand_after,actor_type,occurred_at,reason) SELECT gen_random_uuid(),id,'adjustment',1,1,'staff',now(),' x ' FROM catalog.variants", "ck_mv_reason")]
    [InlineData("INSERT INTO inventory.receipts(id,code,status,created_by) VALUES (gen_random_uuid(),'fixture','posted',gen_random_uuid())", "ck_receipts_row")]
    [InlineData("INSERT INTO inventory.receipts(id,code,status,created_by,posted_at) VALUES (gen_random_uuid(),'fixture','draft',gen_random_uuid(),now())", "ck_receipts_row")]
    public async Task RequiredChecksReturnExactSqlState(string sql, string constraint)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        await ErrorAsync(c, sql, "23514", constraint);
    }

    [Theory]
    [InlineData("image/jpeg", "catalog", 1L, true)]
    [InlineData("image/png", "content", 15728640L, true)]
    [InlineData("image/webp", "branding", 15728640L, true)]
    [InlineData("image/avif", "evidence", 15728640L, true)]
    [InlineData("video/mp4", "evidence", 52428800L, true)]
    [InlineData("video/quicktime", "evidence", 52428800L, true)]
    [InlineData("image/jpeg", "catalog", 0L, false)]
    [InlineData("image/jpeg", "catalog", -1L, false)]
    [InlineData("image/jpeg", "catalog", 15728641L, false)]
    [InlineData("video/mp4", "evidence", 0L, false)]
    [InlineData("video/mp4", "evidence", 52428801L, false)]
    [InlineData("video/mp4", "catalog", 1L, false)]
    [InlineData("video/quicktime", "content", 1L, false)]
    [InlineData("video/mp4", "branding", 1L, false)]
    [InlineData("image/svg+xml", "catalog", 1L, false)]
    [InlineData("text/html", "content", 1L, false)]
    [InlineData("image/png", "unknown", 1L, false)]
    public async Task BothMediaTablesEnforceMimePurposeAndSize(string mime, string purpose, long size, bool allowed)
    {
        var c = await fixture.DatabaseAsync();
        var assets = $"INSERT INTO media.assets(id,storage_key,mime_type,purpose,byte_size) VALUES (gen_random_uuid(),'fixture','{mime}','{purpose}',{size})";
        var uploads = $"INSERT INTO media.upload_sessions(id,storage_key,original_name,mime_type,purpose,expected_bytes,expires_at) VALUES (gen_random_uuid(),'fixture','fake.png','{mime}','{purpose}',{size},now()+interval '1 day')";
        if (allowed) { await ExecuteAsync(c, assets); await ExecuteAsync(c, uploads); }
        else { await ErrorAsync(c, assets, "23514"); await ErrorAsync(c, uploads, "23514", "ck_upload_size_by_kind"); }
    }

    [Fact]
    public async Task UniqueCoverSkuAndExclusionBoundaries()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        await ErrorAsync(c, $"INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) SELECT gen_random_uuid(),product_type_id,sku_base,'another','Another' FROM catalog.products", "23505", "ux_products_sku_base");
        await ErrorAsync(c, "INSERT INTO catalog.variants(id,product_id,sku) SELECT gen_random_uuid(),product_id,sku FROM catalog.variants", "23505", "ux_variants_sku");
        await ExecuteAsync(c, $"INSERT INTO catalog.product_media(id,product_id,asset_id,alt_text,is_cover) VALUES (gen_random_uuid(),'{ProductId}','00000000-0000-0000-0000-000000000005','Fixture photo',true)");
        await ErrorAsync(c, $"INSERT INTO catalog.product_media(id,product_id,asset_id,alt_text,is_cover) VALUES (gen_random_uuid(),'{ProductId}','00000000-0000-0000-0000-000000000006','Other photo',true)", "23505", "ux_product_media_cover");
        var price = $"INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by) VALUES (gen_random_uuid(),'{VariantId}',500,";
        await ExecuteAsync(c, price + "'2026-01-01Z','2026-01-02Z',gen_random_uuid())");
        await ExecuteAsync(c, price + "'2026-01-02Z','2026-01-03Z',gen_random_uuid())");
        await ErrorAsync(c, price + "'2026-01-01T12:00Z','2026-01-02T12:00Z',gen_random_uuid())", "23P01", "ex_sale_prices_no_overlap");
        await ExecuteAsync(c, "INSERT INTO catalog.materials(id,slug,name,kind) VALUES (gen_random_uuid(),'fake-a','Fixture A','clay'),(gen_random_uuid(),'fake-b','Fixture B','clay')");
        Assert.Equal(2, await ScalarAsync<int>(c, "SELECT count(*)::int FROM catalog.materials WHERE sku_code IS NULL"));
    }

    [Fact]
    public async Task RealLoginsEnforceLeastPrivilegeAndReexecutionPreservesIt()
    {
        var c = await fixture.DatabaseAsync();
        var app = DatabaseProvisioning.Login(c, "tp_app");
        var backup = DatabaseProvisioning.Login(c, "tp_readonly");
        await ExecuteAsync(app, Seed);
        await ExecuteAsync(app, "UPDATE catalog.products SET subtitle='Fixture'; DELETE FROM media.assets WHERE storage_key='absent'");
        await ExecuteAsync(app, """
INSERT INTO inventory.stock_levels(variant_id,on_hand) SELECT id,1 FROM catalog.variants;
INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by)
 SELECT gen_random_uuid(),id,1,now(),now()+interval '1 hour',gen_random_uuid() FROM catalog.variants;
INSERT INTO content.redirects(source_path,target_path) VALUES ('/fake-source','/fake-target');
""");
        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT nextval('catalog.sku_number_seq')"));
        Assert.Equal("am tra", await ScalarAsync<string>(app, "SELECT public.f_unaccent('ẤM TRÀ')"));
        await ExecuteAsync(app, $"INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,on_hand_after,actor_type,occurred_at) VALUES (gen_random_uuid(),'{VariantId}','opening',1,1,'system',now())");
        foreach (var table in new[] { "inventory.stock_movements", "platform.audit_logs", "platform.administrative_data_imports" })
            foreach (var sql in new[] { $"UPDATE {table} SET id=id", $"DELETE FROM {table}", $"TRUNCATE {table}" })
                await ErrorAsync(app, sql, "42501");
        foreach (var schema in new[] { "media", "catalog", "inventory", "pricing", "content" })
            await ErrorAsync(app, $"CREATE TABLE {schema}.forbidden(id int)", "42501");
        Assert.False(await ScalarAsync<bool>(app, "SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR rolinherit FROM pg_roles WHERE rolname=current_user"));
        Assert.Equal(1, await ScalarAsync<int>(backup, "SELECT count(*)::int FROM catalog.products"));
        Assert.Equal(1L, await ScalarAsync<long>(backup, "SELECT last_value FROM catalog.sku_number_seq"));
        foreach (var sql in new[] { "INSERT INTO content.redirects(source_path,target_path) VALUES ('/a','/b')", "UPDATE catalog.products SET name=name", "DELETE FROM catalog.products", "SELECT nextval('catalog.sku_number_seq')", "CREATE TABLE catalog.forbidden(id int)" })
            await ErrorAsync(backup, sql, "42501");
        // Reapplying the helper must not grant runtime access to unrelated objects.
        await ExecuteAsync(c, "CREATE TABLE catalog.outside_scope(id integer)");
        await ExecuteAsync(c, GrantsSql.CatalogAndInventory());
        await ErrorAsync(app, "INSERT INTO catalog.outside_scope(id) VALUES (1)", "42501");
        Assert.Equal(1, await ScalarAsync<int>(backup, "SELECT count(*)::int FROM inventory.stock_movements"));
        await ErrorAsync(app, "DELETE FROM inventory.stock_movements", "42501");
        Assert.Equal(2L, await ScalarAsync<long>(app, "SELECT nextval('catalog.sku_number_seq')"));
        // Investigation identity is synthetic and separate from the backup credential.
        await ExecuteAsync(fixture.Admin(c), "CREATE ROLE p101_investigator NOLOGIN NOINHERIT");
        Assert.False(await ScalarAsync<bool>(c, "SELECT pg_has_role('p101_investigator','tp_owner','MEMBER') OR pg_has_role('p101_investigator','tp_readonly','MEMBER') OR has_column_privilege('p101_investigator','iam.staff_users','password_hash','SELECT') OR has_column_privilege('p101_investigator','platform.data_protection_keys','xml','SELECT')"));
        await ExecuteAsync(fixture.Admin(c), "DROP ROLE p101_investigator");
    }

    [Fact]
    public async Task GINUnaccentFixtureAndSequenceBoundaryWithoutGenerator()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        await ExecuteAsync(c, "UPDATE catalog.products SET search_text=public.f_unaccent('Ấm Trà Đất AM-XX-0001 Fixture Type')");
        Assert.Equal("am tra dat am-xx-0001 fixture type", await ScalarAsync<string>(c, "SELECT search_text FROM catalog.products"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM catalog.products WHERE search_text LIKE '%'||public.f_unaccent('TRÀ ĐẤT')||'%'"));
        var index = await ScalarAsync<string>(c, "SELECT indexdef FROM pg_indexes WHERE schemaname='catalog' AND indexname='ix_products_search'");
        Assert.Contains("USING gin (search_text gin_trgm_ops)", index, StringComparison.Ordinal);
        await ExecuteAsync(c, "SELECT setval('catalog.sku_number_seq',9998)");
        Assert.Equal(9999L, await ScalarAsync<long>(c, "SELECT nextval('catalog.sku_number_seq')"));
        Assert.Equal(10000L, await ScalarAsync<long>(c, "SELECT nextval('catalog.sku_number_seq')"));
        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ScalarAsync<long>(c, "SELECT nextval('catalog.sku_number_seq')")));
        Assert.Equal(20, values.Distinct().Count());
        Assert.All(values, value => Assert.InRange(value, 10001L, 10020L));
    }

    [Fact]
    public async Task XminRejectsStaleEfUpdateAndMappingsUseExactTypes()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, Seed);
        await using var first = Context(DatabaseProvisioning.Login(c, "tp_app"));
        await using var second = Context(DatabaseProvisioning.Login(c, "tp_app"));
        var a = await first.Set<Variant>().SingleAsync();
        var b = await second.Set<Variant>().SingleAsync();
        var old = a.Version;
        a.PriceVnd = 10000000000L;
        await first.SaveChangesAsync();
        Assert.NotEqual(old, a.Version);
        b.PriceVnd = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(10000000000L, await ScalarAsync<long>(c, "SELECT price_vnd FROM catalog.variants"));
        foreach (var type in new[] { typeof(Product), typeof(Variant), typeof(Receipt), typeof(Stocktake) })
        {
            var property = first.Model.FindEntityType(type)!.FindProperty("Version")!;
            Assert.Equal(typeof(uint), property.ClrType);
            Assert.Equal("xmin", property.GetColumnName());
            Assert.True(property.IsConcurrencyToken);
            Assert.Equal("xid", property.GetColumnType());
        }
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema IN ('catalog','inventory') AND column_name IN ('xmin','version')"));
        Assert.Equal("bigint", first.Model.FindEntityType(typeof(Variant))!.FindProperty("PriceVnd")!.GetColumnType());
        Assert.Equal("numeric(12,2)", first.Model.FindEntityType(typeof(ProductAttributeValue))!.FindProperty("ValueNumber")!.GetColumnType());
    }

    [Fact]
    public async Task PersistenceAllowsFalseDefaultsNullableUniqueAndEditingUniqueFields()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(DatabaseProvisioning.Login(c, "tp_app"));
        var type = new ProductType { Id = Guid.NewGuid(), Code = "fake_type", Name = "Fixture type", IsActive = false };
        db.Add(type);
        db.Add(new Material { Id = Guid.NewGuid(), Slug = "fake-material", Name = "Fixture material", Kind = "clay" });
        await db.SaveChangesAsync();
        Assert.False(await ScalarAsync<bool>(c, "SELECT is_active FROM catalog.product_types"));
        type.Code = "edited_type";
        await db.SaveChangesAsync();
        Assert.Equal("edited_type", await ScalarAsync<string>(c, "SELECT code FROM catalog.product_types"));
        Assert.True(await ScalarAsync<bool>(c, "SELECT sku_code IS NULL FROM catalog.materials"));
        var product = new Product { Id = Guid.NewGuid(), ProductTypeId = type.Id, SkuBase = "AM-XX-0001", Slug = "original-slug", Name = "Fixture product" };
        db.Add(product);
        await db.SaveChangesAsync();
        product.Slug = "edited-slug";
        await db.SaveChangesAsync();
        Assert.Equal("edited-slug", await ScalarAsync<string>(c, "SELECT slug FROM catalog.products"));
    }

    [Fact]
    public async Task IdempotentScriptExecutesTwiceAndKeepsRowsSequenceAndHistory()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        await using var db = Context(c);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteAsync(c, sql);
        await ExecuteAsync(c, Seed);
        await ExecuteAsync(c, "SELECT nextval('catalog.sku_number_seq')");
        var before = await P0FingerprintAsync(c);
        await ExecuteAsync(c, sql);
        Assert.Equal(before, await P0FingerprintAsync(c));
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM catalog.products"));
        Assert.Equal(2L, await ScalarAsync<long>(c, "SELECT nextval('catalog.sku_number_seq')"));
        await ErrorAsync(DatabaseProvisioning.Login(c, "tp_app"), "TRUNCATE inventory.stock_movements", "42501");
    }

    [Fact]
    public async Task MissingRolePreflightFailsBeforeNewObjectsAndCanRecover()
    {
        // Mutates cluster-level role tp_readonly; must run on its own disposable server.
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var c = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
        await using (var init = Context(c))
            await init.GetService<IMigrator>().MigrateAsync(P0);
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "teapremium_test" }.ConnectionString;
        await ExecuteAsync(admin, "ALTER ROLE tp_readonly RENAME TO p101_missing_backup");
        try
        {
            await using var db = Context(c);
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("infra/postgres/init-roles.sql", error.MessageText, StringComparison.Ordinal);
            Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname='catalog'"));
            Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally { await ExecuteAsync(admin, "ALTER ROLE p101_missing_backup RENAME TO tp_readonly"); }
        await using var recovered = Context(c);
        await recovered.Database.MigrateAsync();
        Assert.Equal(9, (await recovered.Database.GetAppliedMigrationsAsync()).Count());
    }
}
