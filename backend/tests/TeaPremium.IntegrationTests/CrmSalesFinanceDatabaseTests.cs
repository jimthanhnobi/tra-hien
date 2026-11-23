using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CrmSalesFinanceFixture;

namespace TeaPremium.IntegrationTests;

public sealed class CrmSalesFinanceDatabaseTests(CrmSalesFinanceFixture fixture) : IClassFixture<CrmSalesFinanceFixture>
{
    [Fact]
    public async Task ModelHasNoPendingChangesAndMigrationMatchesAuthoringContract()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations.Keys, key => key.EndsWith("_P2_01_CrmSalesFinanceFoundation", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        Assert.Equal(26, migration.UpOperations.OfType<CreateTableOperation>().Count());
        var sequence = Assert.Single(migration.UpOperations.OfType<CreateSequenceOperation>());
        Assert.Equal("crm", sequence.Schema);
        Assert.Equal("customer_code_seq", sequence.Name);
        Assert.Equal(1000L, sequence.StartValue);
        Assert.Empty(migration.UpOperations.OfType<AddColumnOperation>());
        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        Assert.Equal(GrantsSql.Preflight, sql[0].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.Equal(GrantsSql.CrmSalesFinance().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(),
            sql[^1].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.All(migration.UpOperations.OfType<CreateTableOperation>(), table =>
        {
            Assert.DoesNotContain(table.Columns, column => column.Name == "version");
            Assert.All(table.Columns.Where(column => column.Name == "xmin"), column =>
            {
                Assert.True(column.IsRowVersion);
                Assert.Equal("xid", column.ColumnType);
            });
            Assert.True(table.Schema is "crm" or "sales" or "finance" or "pricing");
        });
        var script = db.GetService<IMigrator>().GenerateScript(P1Head);
        Assert.DoesNotMatch(@"\bxmin\s+xid", script);
    }

    [Fact]
    public async Task EmptyAndExistingP1UpgradePreserveDataSequencesAndHistory()
    {
        var connection = await fixture.DatabaseAsync(target: P1Head);
        await ExecuteAsync(connection, P1PrerequisitesSeed + """
INSERT INTO platform.settings(key,value) VALUES ('p201.fixture','{"fake":true}');
INSERT INTO iam.staff_users(id,display_name,is_active,must_change_password,created_at,email,normalized_email,
 email_confirmed,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
 VALUES ('00000000-0000-0000-0000-000000000297','Synthetic staff',true,true,now(),'p201@example.invalid','P201@EXAMPLE.INVALID',
 false,false,false,true,0);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved) VALUES ('00000000-0000-0000-0000-000000000203',5,0);
INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by)
 VALUES ('00000000-0000-0000-0000-000000000296','00000000-0000-0000-0000-000000000203',900000,'2026-01-01Z','2026-02-01Z','00000000-0000-0000-0000-000000000297');
INSERT INTO content.redirects(source_path,target_path) VALUES ('/p201-old','/p201-new');
SELECT setval('catalog.sku_number_seq', 42);
""");
        // C1: infra/postgres/preflight.sql must PASS on P1Head BEFORE P2-01 Up (no "does not exist").
        var preflightFileSql = await File.ReadAllTextAsync(Path.Combine(DatabaseProvisioning.Artifacts, "preflight.sql"));
        await ExecuteAsync(connection, preflightFileSql);
        await ExecuteAsync(connection, GrantsSql.Preflight);

        var before = await P0P1FingerprintAsync(connection);
        await using var db = Context(connection);
        var oldHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(6, oldHistory.Length);
        await db.Database.MigrateAsync();

        // C1: preflight.sql must PASS again AFTER P2-01 Up.
        await ExecuteAsync(connection, preflightFileSql);
        await ExecuteAsync(connection, GrantsSql.Preflight);

        Assert.Equal(before, await P0P1FingerprintAsync(connection));
        Assert.Equal(42L, await ScalarAsync<long>(connection, "SELECT last_value FROM catalog.sku_number_seq"));
        var newHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(oldHistory, newHistory.Take(6));
        Assert.Equal(7, newHistory.Length);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(27, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('crm','sales','finance','pricing')"));
        Assert.Equal(0, await ScalarAsync<int>(connection, "SELECT count(*)::int FROM pg_tables WHERE schemaname='marketing' OR (schemaname='inventory' AND tablename='reservations')"));
        Assert.StartsWith("17.", await ScalarAsync<string>(connection, "SHOW server_version"), StringComparison.Ordinal);

        // Exercise an independent empty database upgrade as well.
        var empty = await fixture.DatabaseAsync();
        Assert.Equal(27, await ScalarAsync<int>(empty, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('crm','sales','finance','pricing')"));
    }

    internal static async Task<string> P0P1FingerprintAsync(string connection) => await ScalarAsync<string>(connection, """
SELECT string_agg(name||':'||data,E'\n' ORDER BY name) FROM (
 SELECT n.nspname||'.'||c.relname name,
   (xpath('/table/row/data/text()',query_to_xml(format(
     'SELECT coalesce(jsonb_agg(j ORDER BY j::text),''[]''::jsonb)::text data FROM (SELECT to_jsonb(t) j FROM %I.%I t) q',n.nspname,c.relname),false,false,'')))[1]::text data
 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname IN ('platform','iam','media','catalog','inventory','content') AND c.relkind='r'
 UNION ALL
 SELECT 'pricing.sale_prices',
   (xpath('/table/row/data/text()',query_to_xml(
     'SELECT coalesce(jsonb_agg(j ORDER BY j::text),''[]''::jsonb)::text data FROM (SELECT to_jsonb(t) j FROM pricing.sale_prices t) q',false,false,'')))[1]::text
) q
""");

    [Fact]
    public async Task IdempotentScriptExecutesTwiceAndPreservesState()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        await using var db = Context(c);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteAsync(c, sql);
        await ExecuteAsync(c, CoreP2Seed);
        Assert.Equal(1000L, await ScalarAsync<long>(c, "SELECT nextval('crm.customer_code_seq')"));
        var before = await P0P1FingerprintAsync(c);
        var p2Before = await ScalarAsync<string>(c, "SELECT code||':'||full_name FROM crm.customers WHERE id='" + CustomerId + "'");
        await ExecuteAsync(c, sql);
        Assert.Equal(before, await P0P1FingerprintAsync(c));
        Assert.Equal(p2Before, await ScalarAsync<string>(c, "SELECT code||':'||full_name FROM crm.customers WHERE id='" + CustomerId + "'"));
        Assert.Equal(7, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1001L, await ScalarAsync<long>(c, "SELECT nextval('crm.customer_code_seq')"));
        await ErrorAsync(DatabaseProvisioning.Login(c, "tp_app"), "DELETE FROM sales.orders", "42501");
    }

    [Fact]
    public async Task RealTpAppLoginAllowsExpectedDmlOrderLinesDeleteAndSequenceNextval()
    {
        var c = await fixture.DatabaseAsync();
        var app = DatabaseProvisioning.Login(c, "tp_app");
        await ExecuteAsync(app, CoreP2Seed);
        await ExecuteAsync(app, $"""
INSERT INTO crm.tags(id,name,color) VALUES ('{TagId}','VIP','#aabbcc');
INSERT INTO crm.customer_tags(customer_id,tag_id) VALUES ('{CustomerId}','{TagId}');
INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default)
 VALUES (gen_random_uuid(),'{CustomerId}','Receiver','+84900000002','79','79001','1 St',true);
INSERT INTO sales.carts(id,token_hash,customer_account_id,expires_at)
 VALUES ('{CartId}','tok-hash-1','{AccountId}',now()+interval '30 days');
INSERT INTO sales.cart_items(cart_id,variant_id,quantity) VALUES ('{CartId}','{VariantId}',2);
INSERT INTO finance.expenses(id,category,amount_vnd,incurred_on,description,created_by)
 VALUES (gen_random_uuid(),'packaging',50000,'2026-11-23','Boxes','{StaffId}');
INSERT INTO pricing.voucher_redemptions(id,voucher_id,order_id,customer_id,amount_vnd)
 VALUES (gen_random_uuid(),'{VoucherId}','{OrderId}','{CustomerId}',100000);
UPDATE crm.customers SET is_vip=true WHERE id='{CustomerId}';
UPDATE sales.orders SET internal_note='Updated by tp_app' WHERE id='{OrderId}';
DELETE FROM sales.order_lines WHERE id='{OrderLineId}';
DELETE FROM sales.cart_items WHERE cart_id='{CartId}';
DELETE FROM sales.carts WHERE id='{CartId}';
DELETE FROM crm.customer_addresses WHERE customer_id='{CustomerId}';
DELETE FROM finance.expenses WHERE description='Boxes';
DELETE FROM pricing.voucher_redemptions WHERE order_id='{OrderId}';
""");
        Assert.Equal(0, await ScalarAsync<int>(app, "SELECT count(*)::int FROM sales.order_lines"));
        Assert.Equal("dang tran hien", await ScalarAsync<string>(app, "SELECT public.f_unaccent('Đặng Trần Hiên')"));
        Assert.Equal(1000L, await ScalarAsync<long>(app, "SELECT nextval('crm.customer_code_seq')"));
        Assert.Equal(1001L, await ScalarAsync<long>(app, "SELECT nextval('crm.customer_code_seq')"));
        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ScalarAsync<long>(app, "SELECT nextval('crm.customer_code_seq')")));
        Assert.Equal(20, values.Distinct().Count());
        Assert.All(values, value => Assert.InRange(value, 1002L, 1021L));
    }

    public static IEnumerable<object[]> TpAppForbiddenCases()
    {
        // 5 protected DELETEs
        foreach (var t in new[] { "sales.orders", "sales.payments", "sales.refunds", "sales.order_status_history", "crm.customers" })
            yield return [$"DELETE FROM {t}", "error"];
        // 26 TRUNCATEs
        foreach (var t in new[]
        {
            "crm.customers", "crm.tags", "crm.customer_tags", "crm.customer_addresses", "crm.customer_accounts",
            "crm.customer_logins", "crm.interactions", "crm.customer_interests", "crm.consents", "crm.data_requests",
            "pricing.vouchers", "pricing.voucher_redemptions",
            "sales.carts", "sales.cart_items", "sales.orders", "sales.order_lines", "sales.order_status_history",
            "sales.cod_settlements", "sales.payments", "sales.return_requests", "sales.return_lines",
            "sales.return_evidence", "sales.refunds", "sales.shipments", "sales.shipment_events",
            "finance.expenses"
        })
            yield return [$"TRUNCATE {t}", "error"];
        // 9 DDL / setval / grant / grant-option cases
        yield return ["CREATE TABLE crm.forbidden(id int)", "error"];
        yield return ["CREATE TABLE sales.forbidden(id int)", "error"];
        yield return ["CREATE TABLE finance.forbidden(id int)", "error"];
        yield return ["CREATE TABLE pricing.forbidden(id int)", "error"];
        yield return ["ALTER TABLE crm.customers ADD COLUMN forbidden text", "error"];
        yield return ["DROP TABLE finance.expenses", "error"];
        yield return ["SELECT setval('crm.customer_code_seq', 2000)", "error"];
        yield return ["GRANT SELECT ON crm.customers TO PUBLIC", "grant_denied"];
        yield return ["__check_grant_option__", "grant_option_false"];
    }

    [Theory]
    [MemberData(nameof(TpAppForbiddenCases))]
    public async Task RealTpAppLoginRejectsProtectedDeletesTruncatesDdlAndGrantOptions(string sql, string mode)
    {
        var c = await fixture.DatabaseAsync();
        var app = DatabaseProvisioning.Login(c, "tp_app");
        if (mode == "grant_option_false")
        {
            var grantable = await ScalarAsync<long>(c, """
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles r ON r.oid=a.grantee
WHERE n.nspname IN ('crm','sales','finance','pricing') AND r.rolname IN ('tp_app','tp_readonly') AND a.is_grantable
""");
            Assert.Equal(0L, grantable);
            return;
        }
        if (mode == "grant_denied")
        {
            await using var cn = new NpgsqlConnection(app);
            await cn.OpenAsync();
            var denied = false;
            cn.Notice += (_, notice) => denied |= notice.Notice.SqlState is "01000" or "01007";
            await using var cmd = new NpgsqlCommand(sql, cn);
            await cmd.ExecuteNonQueryAsync();
            Assert.True(denied);
            var publicGrants = await ScalarAsync<long>(c, """
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(c.relacl) a
WHERE n.nspname IN ('crm','sales','finance','pricing') AND a.grantee=0
""");
            Assert.Equal(0L, publicGrants);
            return;
        }
        await ErrorAsync(app, sql, "42501");
    }

    [Theory]
    [InlineData("INSERT INTO crm.tags(id,name) VALUES (gen_random_uuid(),'blocked')")]
    [InlineData("UPDATE crm.customers SET full_name='blocked'")]
    [InlineData("DELETE FROM crm.tags")]
    [InlineData("TRUNCATE crm.tags")]
    [InlineData("INSERT INTO sales.cod_settlements(id,carrier_name,settled_at,gross_cod_vnd,fee_vnd,net_received_vnd,created_by) VALUES (gen_random_uuid(),'GHN',now(),100,0,100,gen_random_uuid())")]
    [InlineData("UPDATE sales.orders SET internal_note='blocked'")]
    [InlineData("DELETE FROM sales.order_lines")]
    [InlineData("INSERT INTO finance.expenses(id,category,amount_vnd,incurred_on,description,created_by) VALUES (gen_random_uuid(),'other',100,'2026-11-23','x',gen_random_uuid())")]
    [InlineData("UPDATE pricing.vouchers SET status='disabled'")]
    [InlineData("SELECT nextval('crm.customer_code_seq')")]
    [InlineData("CREATE TABLE crm.ro_forbidden(id int)")]
    [InlineData("CREATE TABLE sales.ro_forbidden(id int)")]
    public async Task RealTpReadonlyLoginAllowsSelectOnlyAndRejectsEveryWrite(string forbiddenSql)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, "SELECT nextval('crm.customer_code_seq')");
        var ro = DatabaseProvisioning.Login(c, "tp_readonly");
        foreach (var table in new[]
        {
            "crm.customers", "crm.tags", "crm.customer_tags", "crm.customer_addresses", "crm.customer_accounts",
            "crm.customer_logins", "crm.interactions", "crm.customer_interests", "crm.consents", "crm.data_requests",
            "pricing.vouchers", "pricing.voucher_redemptions",
            "sales.carts", "sales.cart_items", "sales.orders", "sales.order_lines", "sales.order_status_history",
            "sales.cod_settlements", "sales.payments", "sales.return_requests", "sales.return_lines",
            "sales.return_evidence", "sales.refunds", "sales.shipments", "sales.shipment_events",
            "finance.expenses"
        })
        {
            Assert.Equal(0L, await ScalarAsync<long>(ro, $"SELECT count(*) FROM {table}"));
        }
        Assert.Equal(1000L, await ScalarAsync<long>(ro, "SELECT last_value FROM crm.customer_code_seq"));
        await ErrorAsync(ro, forbiddenSql, "42501");
        Assert.Equal(0L, await ScalarAsync<long>(c, """
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles r ON r.oid=a.grantee
WHERE n.nspname IN ('crm','sales','finance','pricing') AND r.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable)
"""));
    }

    [Theory]
    [InlineData("GRANT SELECT ON crm.customers TO tp_readonly WITH GRANT OPTION", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT USAGE ON SEQUENCE crm.customer_code_seq TO tp_app WITH GRANT OPTION", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT USAGE ON SCHEMA crm TO tp_app WITH GRANT OPTION", "Unexpected runtime/backup schema grant option")]
    [InlineData("GRANT DELETE ON sales.orders TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT DELETE ON sales.payments TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT DELETE ON sales.refunds TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT DELETE ON sales.order_status_history TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT DELETE ON crm.customers TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT TRUNCATE ON finance.expenses TO tp_app", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT INSERT ON finance.expenses TO tp_readonly", "Unexpected runtime/backup object privileges")]
    [InlineData("GRANT SELECT(full_name) ON crm.customers TO tp_app", "Unexpected column grants")]
    [InlineData("GRANT SELECT ON crm.customers TO PUBLIC", "Unexpected PUBLIC object privileges")]
    public async Task PreflightRejectsPrivilegeAndGrantOptionDriftInPhase2Schemas(string driftSql, string expectedMessage)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, driftSql);
        var aclBefore = await ScalarAsync<string>(c, """
SELECT coalesce(jsonb_agg(jsonb_build_array(c.oid,c.relacl::text) ORDER BY c.oid)::text,'[]')
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('crm','sales','finance','pricing')
""");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(c, GrantsSql.Preflight));
        Assert.Contains(expectedMessage, ex.MessageText, StringComparison.Ordinal);
        await using var db = Context(c);
        var frozen = db.GetService<IMigrator>().GenerateScript(P1Head);
        var exFrozen = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(c, frozen));
        Assert.Contains(expectedMessage, exFrozen.MessageText, StringComparison.Ordinal);
        var aclAfter = await ScalarAsync<string>(c, """
SELECT coalesce(jsonb_agg(jsonb_build_array(c.oid,c.relacl::text) ORDER BY c.oid)::text,'[]')
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('crm','sales','finance','pricing')
""");
        Assert.Equal(aclBefore, aclAfter);
    }

    [Theory]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,origin) VALUES ('00000000-0000-0000-0000-000000000301','KH001001','A','+84900000011',NULL,'admin')", true, null)]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,origin) VALUES ('00000000-0000-0000-0000-000000000302','KH001002','B',NULL,'zalo-b','admin')", true, null)]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,contact_status,origin) VALUES ('00000000-0000-0000-0000-000000000303','KH001003','Erased',NULL,NULL,'erased','admin')", true, null)]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,contact_status,merged_into_customer_id,origin) VALUES ('00000000-0000-0000-0000-000000000304','KH001004','Merged',NULL,NULL,'active','00000000-0000-0000-0000-000000000211','admin')", true, null)]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,contact_status,origin) VALUES ('00000000-0000-0000-0000-000000000305','KH001005','BadActive',NULL,NULL,'active','admin')", false, "ck_customer_contact")]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,zalo_name,contact_status,origin) VALUES ('00000000-0000-0000-0000-000000000306','KH001006','BadDnc',NULL,NULL,'do_not_contact','admin')", false, "ck_customer_contact")]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,referred_by_customer_id,origin) VALUES ('00000000-0000-0000-0000-000000000307','KH001007','SelfRef','+84900000017','00000000-0000-0000-0000-000000000307','admin')", false, "ck_customer_not_self_ref")]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,referred_by_customer_id,origin) VALUES ('00000000-0000-0000-0000-000000000308','KH001008','OtherRef','+84900000018','00000000-0000-0000-0000-000000000211','admin')", true, null)]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('00000000-0000-0000-0000-000000000309','KH001009','AccBad','+84900000019','admin'); INSERT INTO crm.customer_accounts(id,customer_id,login_phone,status,security_stamp) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000309',NULL,'active','st')", false, "ck_account_login_phone")]
    [InlineData("INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('00000000-0000-0000-0000-000000000310','KH001010','AccD1','+84900000020','admin'),('00000000-0000-0000-0000-000000000311','KH001011','AccD2','+84900000021','admin'); INSERT INTO crm.customer_accounts(id,customer_id,login_phone,status,security_stamp) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000310',NULL,'disabled','s1'),(gen_random_uuid(),'00000000-0000-0000-0000-000000000311',NULL,'disabled','s2')", true, null)]
    public async Task CustomerContactNotSelfRefAndAccountPhoneChecksEnforceAllCases(string sql, bool allowed, string? constraint)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        if (allowed) await ExecuteAsync(c, sql);
        else await ErrorAsync(c, sql, "23514", constraint);
    }

    [Fact]
    public async Task CrmPartialAndExpressionUniqueIndexesEnforceExactRules()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        // ux_customers_phone: duplicate active phone fails; merged or null phone allowed.
        await ErrorAsync(c, "INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES (gen_random_uuid(),'KH001050','Dup','+84900000001','admin')", "23505", "ux_customers_phone");
        await ExecuteAsync(c, $"""
INSERT INTO crm.customers(id,code,full_name,phone_e164,merged_into_customer_id,origin) VALUES
 (gen_random_uuid(),'KH001051','MergedSamePhone','+84900000001','{CustomerId}','admin');
INSERT INTO crm.customers(id,code,full_name,zalo_name,origin) VALUES
 (gen_random_uuid(),'KH001052','ZaloOnly1','zalo1','admin'),
 (gen_random_uuid(),'KH001053','ZaloOnly2','zalo2','admin');
""");
        // ux_customer_default_address: 2 default addresses for same customer fails; non-default & other customer ok.
        await ExecuteAsync(c, $"""
INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default) VALUES
 (gen_random_uuid(),'{CustomerId}','R1','+84900000002','79','79001','Addr 1',true),
 (gen_random_uuid(),'{CustomerId}','R2','+84900000002','79','79001','Addr 2',false),
 (gen_random_uuid(),'{CustomerId}','R3','+84900000002','79','79001','Addr 3',false);
""");
        await ErrorAsync(c, $"INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default) VALUES (gen_random_uuid(),'{CustomerId}','R4','+84900000002','79','79001','Addr 4',true)", "23505", "ux_customer_default_address");
        // ux_account_email: case-insensitive email uniqueness.
        await ExecuteAsync(c, $"UPDATE crm.customer_accounts SET email='Tea@Example.test' WHERE id='{AccountId}'");
        await ExecuteAsync(c, "INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('00000000-0000-0000-0000-000000000350','KH001054','Cust2','+84900000054','admin')");
        await ErrorAsync(c, "INSERT INTO crm.customer_accounts(id,customer_id,login_phone,email,security_stamp) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000350','+84900000054','tea@example.test','s2')", "23505", "ux_account_email");
        // ux_customer_interest: coalesced expression unique index.
        await ExecuteAsync(c, $"INSERT INTO crm.customer_interests(id,customer_id,interest_type,price_band,source) VALUES (gen_random_uuid(),'{CustomerId}','price_band','1m_3m','manual')");
        await ErrorAsync(c, $"INSERT INTO crm.customer_interests(id,customer_id,interest_type,price_band,source) VALUES (gen_random_uuid(),'{CustomerId}','price_band','1m_3m','manual')", "23505", "ux_customer_interest");
    }

    [Fact]
    public async Task SalesPartialUniqueAndOrderCodeConstraintsEnforceExactRules()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        // ux_orders_code & ck_orders_code
        await ErrorAsync(c, $"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at) VALUES
 (gen_random_uuid(),'TP261123AB12','admin','phone','{CustomerId}','pending','cod',
 'A','+84900000001','B','+84900000002','79','P','W','Addr',1000,0,0,1000,'direct',false,now())
""", "23505", "ux_orders_code");
        await ErrorAsync(c, $"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at) VALUES
 (gen_random_uuid(),'BAD-CODE','admin','phone','{CustomerId}','pending','cod',
 'A','+84900000001','B','+84900000002','79','P','W','Addr',1000,0,0,1000,'direct',false,now())
""", "23514", "ck_orders_code");
        // ux_cart_account
        await ExecuteAsync(c, $"INSERT INTO sales.carts(id,token_hash,customer_account_id,expires_at) VALUES (gen_random_uuid(),'tok-a','{AccountId}',now()+interval '1 day')");
        await ErrorAsync(c, $"INSERT INTO sales.carts(id,token_hash,customer_account_id,expires_at) VALUES (gen_random_uuid(),'tok-b','{AccountId}',now()+interval '1 day')", "23505", "ux_cart_account");
        // ux_payments_bank_ref: duplicate active fails, re-recording after voided succeeds.
        await ExecuteAsync(c, $"INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,bank_reference,confirmed_at) VALUES ('00000000-0000-0000-0000-000000000361','{OrderId}','bank_transfer','sepay',100000,'confirmed','FT123',now())");
        await ErrorAsync(c, $"INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,bank_reference,confirmed_at) VALUES (gen_random_uuid(),'{OrderId}','bank_transfer','sepay',100000,'confirmed','FT123',now())", "23505", "ux_payments_bank_ref");
        await ExecuteAsync(c, "UPDATE sales.payments SET status='voided',confirmed_at=NULL,void_reason='duplicate test' WHERE id='00000000-0000-0000-0000-000000000361'");
        await ExecuteAsync(c, $"INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,bank_reference,confirmed_at) VALUES (gen_random_uuid(),'{OrderId}','bank_transfer','sepay',100000,'confirmed','FT123',now())");
        // ux_shipment_active_order & ux_shipment_tracking
        await ExecuteAsync(c, $"INSERT INTO sales.shipments(id,order_id,purpose,carrier_name,tracking_code,status) VALUES ('{ShipmentId}','{OrderId}','order','GHN','GHN001','created')");
        await ErrorAsync(c, $"INSERT INTO sales.shipments(id,order_id,purpose,carrier_name,tracking_code,status) VALUES (gen_random_uuid(),'{OrderId}','order','GHN','GHN002','created')", "23505", "ux_shipment_active_order");
        await ExecuteAsync(c, $"UPDATE sales.shipments SET status='cancelled' WHERE id='{ShipmentId}'");
        await ErrorAsync(c, $"INSERT INTO sales.shipments(id,order_id,purpose,carrier_name,tracking_code,status) VALUES (gen_random_uuid(),'{OrderId}','order','GHN','GHN001','created')", "23505", "ux_shipment_tracking");
        await ExecuteAsync(c, $"INSERT INTO sales.shipments(id,order_id,purpose,carrier_name,tracking_code,status) VALUES (gen_random_uuid(),'{OrderId}','order','GHN','GHN002','created')");
    }

    [Theory]
    [InlineData("UPDATE sales.orders SET total_vnd=999999 WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_total")]
    [InlineData("UPDATE sales.orders SET discount_vnd=1000001,total_vnd=29999 WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_discount")]
    [InlineData("UPDATE sales.orders SET status='delivered' WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_delivered")]
    [InlineData("UPDATE sales.orders SET status='cancelled' WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_cancelled")]
    [InlineData("UPDATE sales.orders SET origin='web',sales_channel='phone' WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_web_channel")]
    [InlineData("UPDATE sales.orders SET origin='web',sales_channel='web',payment_method='bank_transfer',payment_due_at=NULL WHERE id='00000000-0000-0000-0000-000000000214'", "ck_order_due")]
    [InlineData("UPDATE sales.order_lines SET line_total_vnd=500 WHERE id='00000000-0000-0000-0000-000000000215'", "ck_line_total")]
    [InlineData("UPDATE sales.order_lines SET price_source='override',price_override_reason='ab' WHERE id='00000000-0000-0000-0000-000000000215'", "ck_line_override")]
    [InlineData("UPDATE sales.order_lines SET returned_quantity=2 WHERE id='00000000-0000-0000-0000-000000000215'", "ck_line_returned")]
    [InlineData("INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,starts_at,ends_at) VALUES (gen_random_uuid(),'BADDATE','manual','fixed',1000,'2026-05-01Z','2026-05-01Z')", "ck_vouchers_dates")]
    [InlineData("INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,starts_at,ends_at) VALUES (gen_random_uuid(),'BADPCT','manual','percent',51,'2026-05-01Z','2026-06-01Z')", "ck_vouchers_percent_bounds")]
    [InlineData("INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,starts_at,ends_at,usage_limit,used_count) VALUES (gen_random_uuid(),'BADCAP','manual','fixed',1000,'2026-05-01Z','2026-06-01Z',1,2)", "ck_vouchers_usage_cap")]
    [InlineData("INSERT INTO sales.cod_settlements(id,carrier_name,settled_at,gross_cod_vnd,fee_vnd,net_received_vnd,created_by) VALUES (gen_random_uuid(),'GHN',now(),1000,100,800,gen_random_uuid())", "ck_cod_settlements_row")]
    [InlineData("INSERT INTO sales.payments(id,order_id,method,amount_vnd,status,confirmed_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','cash',1000,'confirmed',NULL)", "ck_payments_confirmed")]
    [InlineData("INSERT INTO sales.payments(id,order_id,method,amount_vnd,status) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','bank_transfer',1000,'awaiting_settlement')", "ck_payments_awaiting_cod")]
    [InlineData("INSERT INTO sales.payments(id,order_id,method,amount_vnd,status,void_reason) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','cash',1000,'voided',NULL)", "ck_payments_void_reason")]
    [InlineData("UPDATE sales.return_requests SET status='approved',resolution=NULL WHERE id='00000000-0000-0000-0000-000000000216'", "ck_return_requests_row")]
    [InlineData("INSERT INTO sales.shipments(id,order_id,purpose,return_request_id,carrier_name) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','replacement',NULL,'GHN')", "ck_shipments_row")]
    [InlineData("INSERT INTO finance.expenses(id,category,amount_vnd,incurred_on,channel,description,created_by) VALUES (gen_random_uuid(),'marketing',1000,'2026-11-23',NULL,'Ads',gen_random_uuid())", "ck_expenses_marketing_channel")]
    [InlineData("INSERT INTO finance.expenses(id,category,amount_vnd,incurred_on,description,created_by) VALUES (gen_random_uuid(),'fixed',1000,'2026-11-15','Rent',gen_random_uuid())", "ck_expenses_fixed_first_day")]
    public async Task SalesPricingAndFinanceChecksRejectInvalidRowsWith23514(string sql, string constraint)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        await ErrorAsync(c, sql, "23514", constraint);
    }

    [Fact]
    public async Task GinCustomerSearchIndexSupportsUnaccentVietnameseLookup()
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        var indexDef = await ScalarAsync<string>(c, "SELECT indexdef FROM pg_indexes WHERE schemaname='crm' AND indexname='ix_customers_search'");
        Assert.Contains("USING gin (search_text gin_trgm_ops)", indexDef, StringComparison.Ordinal);
        await ExecuteAsync(c, $"UPDATE crm.customers SET full_name='Đặng Trần Hiên',search_text=public.f_unaccent('Đặng Trần Hiên zalo-hien') WHERE id='{CustomerId}'");
        Assert.Equal("dang tran hien zalo-hien", await ScalarAsync<string>(c, $"SELECT search_text FROM crm.customers WHERE id='{CustomerId}'"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM crm.customers WHERE search_text LIKE '%'||public.f_unaccent('dang tran')||'%'"));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Order")]
    [InlineData("ReturnRequest")]
    [InlineData("Voucher")]
    public async Task XminOptimisticConcurrencyThrowsOnStaleWriteForAllFourEntities(string entityName)
    {
        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, CoreP2Seed);
        var app = DatabaseProvisioning.Login(c, "tp_app");
        await using var first = Context(app);
        await using var second = Context(app);

        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema IN ('crm','sales','pricing','finance') AND column_name IN ('xmin','version')"));

        switch (entityName)
        {
            case "Customer":
                {
                    var a = await first.Set<Customer>().SingleAsync();
                    var b = await second.Set<Customer>().SingleAsync();
                    var old = a.Version;
                    a.NoteSummary = "Winner";
                    await first.SaveChangesAsync();
                    Assert.NotEqual(old, a.Version);
                    b.NoteSummary = "Stale";
                    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
                    Assert.Equal("Winner", await ScalarAsync<string>(c, "SELECT note_summary FROM crm.customers"));
                    break;
                }
            case "Order":
                {
                    var a = await first.Set<Order>().SingleAsync();
                    var b = await second.Set<Order>().SingleAsync();
                    var old = a.Version;
                    a.InternalNote = "Winner";
                    await first.SaveChangesAsync();
                    Assert.NotEqual(old, a.Version);
                    b.InternalNote = "Stale";
                    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
                    Assert.Equal("Winner", await ScalarAsync<string>(c, "SELECT internal_note FROM sales.orders"));
                    break;
                }
            case "ReturnRequest":
                {
                    var a = await first.Set<ReturnRequest>().SingleAsync();
                    var b = await second.Set<ReturnRequest>().SingleAsync();
                    var old = a.Version;
                    a.DecisionNote = "Winner";
                    await first.SaveChangesAsync();
                    Assert.NotEqual(old, a.Version);
                    b.DecisionNote = "Stale";
                    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
                    Assert.Equal("Winner", await ScalarAsync<string>(c, "SELECT decision_note FROM sales.return_requests"));
                    break;
                }
            case "Voucher":
                {
                    var a = await first.Set<Voucher>().SingleAsync();
                    var b = await second.Set<Voucher>().SingleAsync();
                    var old = a.Version;
                    a.MinOrderVnd = 500000;
                    await first.SaveChangesAsync();
                    Assert.NotEqual(old, a.Version);
                    b.MinOrderVnd = 250000;
                    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
                    Assert.Equal(500000L, await ScalarAsync<long>(c, "SELECT min_order_vnd FROM pricing.vouchers"));
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(entityName));
        }
    }
}
