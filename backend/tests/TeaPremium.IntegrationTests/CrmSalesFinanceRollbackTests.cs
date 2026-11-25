using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static TeaPremium.IntegrationTests.CrmSalesFinanceFixture;

namespace TeaPremium.IntegrationTests;

public sealed class CrmSalesFinanceRollbackTests(CrmSalesFinanceFixture fixture) : IClassFixture<CrmSalesFinanceFixture>
{
    [Fact]
    public async Task EmptyDownToP1LeavesZeroLeftoverSchemasOrGrantsAndUpAgainWorks()
    {
        var p1Reference = await fixture.DatabaseAsync(target: P1Head);
        var p1AclSnapshot = await AclSnapshotAsync(p1Reference);

        var c = await fixture.DatabaseAsync();
        await ExecuteAsync(c, P1PrerequisitesSeed + "INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by) VALUES (gen_random_uuid(),'" + VariantId + "',900000,'2026-01-01Z','2026-02-01Z',gen_random_uuid());");
        var p1DataBefore = await CrmSalesFinanceDatabaseTests.P0P1FingerprintAsync(c);

        await using var db = Context(c);
        await db.GetService<IMigrator>().MigrateAsync(P201Head);
        Assert.Equal(7, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await db.GetService<IMigrator>().MigrateAsync(P1Head);

        Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(p1DataBefore, await CrmSalesFinanceDatabaseTests.P0P1FingerprintAsync(c));
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regnamespace('crm') IS NULL AND to_regnamespace('sales') IS NULL AND to_regnamespace('finance') IS NULL"));
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regnamespace('pricing') IS NOT NULL AND to_regclass('pricing.sale_prices') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('pricing.vouchers') IS NULL AND to_regclass('pricing.voucher_redemptions') IS NULL AND to_regclass('inventory.reservations') IS NULL"));
        Assert.Equal(p1AclSnapshot, await AclSnapshotAsync(c));

        await db.Database.MigrateAsync();
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(p1DataBefore, await CrmSalesFinanceDatabaseTests.P0P1FingerprintAsync(c));
        Assert.Equal(27, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('crm','sales','finance','pricing')"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname='inventory' AND tablename='reservations'"));
        Assert.Equal(1000L, await ScalarAsync<long>(DatabaseProvisioning.Login(c, "tp_app"), "SELECT nextval('crm.customer_code_seq')"));
    }

    public static IEnumerable<object[]> OwnedTableDataSeeds()
    {
        const string cust = "INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('00000000-0000-0000-0000-000000000211','KH001000','C','+84900000001','admin');";
        const string tag = "INSERT INTO crm.tags(id,name) VALUES ('00000000-0000-0000-0000-000000000219','T1');";
        const string acct = cust + "INSERT INTO crm.customer_accounts(id,customer_id,login_phone,security_stamp) VALUES ('00000000-0000-0000-0000-000000000212','00000000-0000-0000-0000-000000000211','+84900000001','st');";
        const string voucher = "INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,starts_at,ends_at) VALUES ('00000000-0000-0000-0000-000000000213','VCH1','manual','fixed',1000,'2026-01-01Z','2027-01-01Z');";
        const string order = cust + "INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_name,address_line,subtotal_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at) VALUES ('00000000-0000-0000-0000-000000000214','TP261123AB12','admin','phone','00000000-0000-0000-0000-000000000211','pending','cod','C','+84900000001','R','+84900000002','79','P','W','Addr',1000,1000,'direct',true,now());";
        const string line = P1PrerequisitesSeed + order + "INSERT INTO sales.order_lines(id,order_id,variant_id,product_id,sku,product_name,quantity,list_price_vnd,unit_price_vnd,price_source,line_total_vnd) VALUES ('00000000-0000-0000-0000-000000000215','00000000-0000-0000-0000-000000000214','00000000-0000-0000-0000-000000000203','00000000-0000-0000-0000-000000000202','SKU','P',1,1000,1000,'list',1000);";
        const string ret = order + "INSERT INTO sales.return_requests(id,code,order_id,reason,description,requested_at) VALUES ('00000000-0000-0000-0000-000000000216','DT-1','00000000-0000-0000-0000-000000000214','defective','crack',now());";
        const string cart = "INSERT INTO sales.carts(id,token_hash,expires_at) VALUES ('00000000-0000-0000-0000-000000000217','tok',now()+interval '1 day');";
        const string ship = order + "INSERT INTO sales.shipments(id,order_id,carrier_name) VALUES ('00000000-0000-0000-0000-000000000218','00000000-0000-0000-0000-000000000214','GHN');";

        yield return ["crm.customers", cust];
        yield return ["crm.tags", tag];
        yield return ["crm.customer_tags", cust + tag + "INSERT INTO crm.customer_tags(customer_id,tag_id) VALUES ('00000000-0000-0000-0000-000000000211','00000000-0000-0000-0000-000000000219');"];
        yield return ["crm.customer_addresses", P1PrerequisitesSeed + cust + "INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000211','R','+84900000002','79','79001','Addr');"];
        yield return ["crm.customer_accounts", acct];
        yield return ["crm.customer_logins", acct + "INSERT INTO crm.customer_logins(provider,provider_key,account_id) VALUES ('google','g1','00000000-0000-0000-0000-000000000212');"];
        yield return ["crm.interactions", cust + "INSERT INTO crm.interactions(id,customer_id,staff_id,channel,kind,content,occurred_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000211',gen_random_uuid(),'phone','note','hi',now());"];
        yield return ["crm.customer_interests", cust + "INSERT INTO crm.customer_interests(id,customer_id,interest_type,price_band,source) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000211','price_band','under_1m','manual');"];
        yield return ["crm.consents", cust + "INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000211','sms','customer_care','granted','staff','ok1',now());"];
        yield return ["crm.data_requests", cust + "INSERT INTO crm.data_requests(id,customer_id,request_type,channel,requested_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000211','export','email',now());"];
        yield return ["pricing.vouchers", voucher];
        yield return ["pricing.voucher_redemptions", voucher + order + "INSERT INTO pricing.voucher_redemptions(id,voucher_id,order_id,customer_id,amount_vnd) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000213','00000000-0000-0000-0000-000000000214','00000000-0000-0000-0000-000000000211',1000);"];
        yield return ["sales.carts", cart];
        yield return ["sales.cart_items", P1PrerequisitesSeed + cart + "INSERT INTO sales.cart_items(cart_id,variant_id,quantity) VALUES ('00000000-0000-0000-0000-000000000217','00000000-0000-0000-0000-000000000203',1);"];
        yield return ["sales.orders", order];
        yield return ["sales.order_lines", line];
        yield return ["sales.order_status_history", order + "INSERT INTO sales.order_status_history(id,order_id,to_status,actor_type,occurred_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','pending','system',now());"];
        yield return ["sales.cod_settlements", "INSERT INTO sales.cod_settlements(id,carrier_name,settled_at,gross_cod_vnd,fee_vnd,net_received_vnd,created_by) VALUES (gen_random_uuid(),'GHN',now(),1000,0,1000,gen_random_uuid());"];
        yield return ["sales.payments", order + "INSERT INTO sales.payments(id,order_id,method,amount_vnd,status,confirmed_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214','cash',1000,'confirmed',now());"];
        yield return ["sales.return_requests", ret];
        yield return ["sales.return_lines", line + "INSERT INTO sales.return_requests(id,code,order_id,reason,description,requested_at) VALUES ('00000000-0000-0000-0000-000000000216','DT-1','00000000-0000-0000-0000-000000000214','defective','crack',now()); INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000216','00000000-0000-0000-0000-000000000215',1);"];
        yield return ["sales.return_evidence", P1PrerequisitesSeed + ret + "INSERT INTO sales.return_evidence(return_request_id,asset_id) VALUES ('00000000-0000-0000-0000-000000000216','00000000-0000-0000-0000-000000000205');"];
        yield return ["sales.refunds", order + "INSERT INTO sales.refunds(id,order_id,amount_vnd,method,reason,refunded_at,created_by) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000214',1000,'cash','r',now(),gen_random_uuid());"];
        yield return ["sales.shipments", ship];
        yield return ["sales.shipment_events", ship + "INSERT INTO sales.shipment_events(id,shipment_id,status,source,occurred_at) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000218','created','manual',now());"];
        yield return ["finance.expenses", "INSERT INTO finance.expenses(id,category,amount_vnd,incurred_on,description,created_by) VALUES (gen_random_uuid(),'other',1000,'2026-11-23','d',gen_random_uuid());"];
    }

    [Theory]
    [MemberData(nameof(OwnedTableDataSeeds))]
    public async Task DownWithDataInAnyOwnedTableRefusesBeforeAnyDrop(string label, string seed)
    {
        var c = await fixture.DatabaseAsync(target: P201Head);
        await ExecuteAsync(c, seed);
        Assert.NotEmpty(label);
        var before = await ShapeAsync(c);
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P1Head));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("P2-01 Down refused: data in", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(before, await ShapeAsync(c));
        Assert.Equal(7, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Theory]
    [InlineData("CREATE TABLE crm.outside_scope(id int)", "DROP TABLE crm.outside_scope")]
    [InlineData("CREATE TABLE sales.outside_scope(id int)", "DROP TABLE sales.outside_scope")]
    [InlineData("CREATE TABLE finance.outside_scope(id int)", "DROP TABLE finance.outside_scope")]
    [InlineData("CREATE TABLE pricing.outside_scope(id int)", "DROP TABLE pricing.outside_scope")]
    [InlineData("CREATE VIEW public.p201_outside AS SELECT id FROM crm.customers", "DROP VIEW public.p201_outside")]
    [InlineData("CREATE FUNCTION public.p201_outside() RETURNS SETOF sales.orders LANGUAGE sql AS 'SELECT * FROM sales.orders'", "DROP FUNCTION public.p201_outside()")]
    [InlineData("CREATE TABLE public.p201_outside(id uuid REFERENCES crm.customers(id))", "DROP TABLE public.p201_outside")]
    [InlineData("CREATE TABLE public.p201_outside(id bigint DEFAULT nextval('crm.customer_code_seq'))", "DROP TABLE public.p201_outside")]
    [InlineData("CREATE COLLATION crm.p201_outside FROM pg_catalog.\"C\"", "DROP COLLATION crm.p201_outside")]
    public async Task DownWithOutsideObjectOrDependencyRefusesAndRecovers(string create, string remove)
    {
        var c = await fixture.DatabaseAsync(target: P201Head);
        await ExecuteAsync(c, create);
        var before = await ShapeAsync(c);
        await using var db = Context(c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P1Head));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("object/dependency outside", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(before, await ShapeAsync(c));
        Assert.Equal(7, (await db.Database.GetAppliedMigrationsAsync()).Count());

        await ExecuteAsync(c, remove);
        await db.GetService<IMigrator>().MigrateAsync(P1Head);
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regnamespace('crm') IS NULL AND to_regnamespace('sales') IS NULL AND to_regnamespace('finance') IS NULL"));
        await db.Database.MigrateAsync();
        Assert.Equal(27, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN ('crm','sales','finance','pricing')"));
    }

    private static Task<string> ShapeAsync(string c) => ScalarAsync<string>(c, """
SELECT string_agg(n.nspname||'.'||c.relname||':'||c.oid,E'\n' ORDER BY n.nspname,c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('crm','sales','finance','pricing')
""");

    private static Task<string> AclSnapshotAsync(string c) => ScalarAsync<string>(c, """
SELECT concat(
 (SELECT coalesce(string_agg(n.nspname||':'||coalesce(n.nspacl::text,''),',' ORDER BY n.nspname),'')
  FROM pg_namespace n WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'),
 '|',
 (SELECT coalesce(string_agg(n.nspname||'.'||d.defaclobjtype::text||':'||coalesce(d.defaclacl::text,''),',' ORDER BY n.nspname,d.defaclobjtype::text),'')
  FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid=d.defaclnamespace),
 '|',
 (SELECT coalesce(string_agg(n.nspname||'.'||c.relname||':'||coalesce(c.relacl::text,''),',' ORDER BY n.nspname,c.relname),'')
  FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='pricing' AND c.relkind='r'))
""");
}
