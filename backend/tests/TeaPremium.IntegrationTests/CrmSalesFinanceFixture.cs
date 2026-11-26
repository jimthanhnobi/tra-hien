using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class CrmSalesFinanceFixture : IAsyncLifetime
{
    private SharedPostgresServer _server = null!;
    private readonly ConcurrentBag<string> _databases = [];
    public const string P1Head = "202611191437_P1_04_TaxonomyPublicationHistory";
    public const string P201Head = "202611230421_P2_01_CrmSalesFinanceFoundation";
    public const string P205Head = "202611230721_P2_05_InventoryReservations";
    public const string P207Head = "202611260800_P2_07_RefundClassificationAndShipping";

    public async Task InitializeAsync() => _server = await SharedPostgresServer.GetAsync();

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
            await _server.DropDatabaseAsync(database);
    }

    public async Task<string> DatabaseAsync(bool migrate = true, string? target = null)
    {
        var name = "teapremium_test_p201_" + Guid.NewGuid().ToString("N");
        var templateKey = !migrate
            ? SharedPostgresServer.EmptyKey
            : target ?? SharedPostgresServer.LatestKey;
        var owner = await _server.CloneAsync(templateKey, name);
        _databases.Add(name);
        return owner;
    }

    public string Admin(string owner) => new NpgsqlConnectionStringBuilder(_server.AdminConnection)
    {
        Database = new NpgsqlConnectionStringBuilder(owner).Database
    }.ConnectionString;

    public static AppDbContext Context(string connection) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);

    public static async Task ExecuteAsync(string connection, string sql)
    {
        await using var cn = new NpgsqlConnection(connection);
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connection, string sql)
    {
        await using var cn = new NpgsqlConnection(connection);
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task ErrorAsync(string connection, string sql, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.Equal(state, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
    }

    public const string ProvinceCode = "79";
    public const string WardCode = "79001";
    public const string ProductId = "00000000-0000-0000-0000-000000000202";
    public const string VariantId = "00000000-0000-0000-0000-000000000203";
    public const string AssetId = "00000000-0000-0000-0000-000000000205";
    public const string StaffId = "00000000-0000-0000-0000-000000000209";
    public const string CustomerId = "00000000-0000-0000-0000-000000000211";
    public const string AccountId = "00000000-0000-0000-0000-000000000212";
    public const string VoucherId = "00000000-0000-0000-0000-000000000213";
    public const string OrderId = "00000000-0000-0000-0000-000000000214";
    public const string OrderLineId = "00000000-0000-0000-0000-000000000215";
    public const string ReturnRequestId = "00000000-0000-0000-0000-000000000216";
    public const string CartId = "00000000-0000-0000-0000-000000000217";
    public const string ShipmentId = "00000000-0000-0000-0000-000000000218";
    public const string TagId = "00000000-0000-0000-0000-000000000219";

    // Synthetic fixtures only (no real PII).
    public const string P1PrerequisitesSeed = """
INSERT INTO platform.provinces(code,name) VALUES ('79','Fixture Province') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Fixture Ward') ON CONFLICT DO NOTHING;
INSERT INTO catalog.product_types(id,code,name) VALUES ('00000000-0000-0000-0000-000000000201','teapot_p2','Fixture type') ON CONFLICT DO NOTHING;
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES
 ('00000000-0000-0000-0000-000000000202','00000000-0000-0000-0000-000000000201','AM-XX-0202','fixture-pot-p2','Fixture pot P2') ON CONFLICT DO NOTHING;
INSERT INTO catalog.variants(id,product_id,sku,price_vnd) VALUES
 ('00000000-0000-0000-0000-000000000203','00000000-0000-0000-0000-000000000202','AM-XX-0202',1000000) ON CONFLICT DO NOTHING;
INSERT INTO media.assets(id,storage_key,mime_type,byte_size,purpose) VALUES
 ('00000000-0000-0000-0000-000000000205','fixture-p2-evidence','image/jpeg',100,'evidence') ON CONFLICT DO NOTHING;
""";

    public const string CoreP2Seed = P1PrerequisitesSeed + """
INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES
 ('00000000-0000-0000-0000-000000000211','KH001000','Fixture Customer','+84900000001','admin');
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,security_stamp) VALUES
 ('00000000-0000-0000-0000-000000000212','00000000-0000-0000-0000-000000000211','+84900000001','fake-stamp');
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,starts_at,ends_at) VALUES
 ('00000000-0000-0000-0000-000000000213','TEA100','manual','fixed',100000,'2026-01-01Z','2027-01-01Z');
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,customer_account_id,status,payment_method,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at) VALUES
 ('00000000-0000-0000-0000-000000000214','TP261123AB12','admin','zalo_personal','00000000-0000-0000-0000-000000000211','00000000-0000-0000-0000-000000000212',
 'pending','cod','Fixture Customer','+84900000001','Fixture Receiver','+84900000002','79','Fixture Province','79001','Fixture Ward','1 Fixture St',
 1000000,0,30000,1030000,'zalo_personal',true,'2026-11-23T00:00:00Z');
INSERT INTO sales.order_lines(id,order_id,variant_id,product_id,sku,product_name,quantity,list_price_vnd,unit_price_vnd,price_source,line_total_vnd) VALUES
 ('00000000-0000-0000-0000-000000000215','00000000-0000-0000-0000-000000000214','00000000-0000-0000-0000-000000000203','00000000-0000-0000-0000-000000000202',
 'AM-XX-0202','Fixture pot P2',1,1000000,1000000,'list',1000000);
INSERT INTO sales.return_requests(id,code,order_id,reason,description,requested_at) VALUES
 ('00000000-0000-0000-0000-000000000216','DT-261123-001','00000000-0000-0000-0000-000000000214','defective','Fixture crack','2026-11-23T01:00:00Z');
""";
}
