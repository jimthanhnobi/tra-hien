using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class CatalogInventoryFixture : IAsyncLifetime
{
    private SharedPostgresServer _server = null!;
    private readonly ConcurrentBag<string> _databases = [];
    public const string P0 = "202611180710_ReconcileRuntimeGrants";

    public async Task InitializeAsync() => _server = await SharedPostgresServer.GetAsync();

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
            await _server.DropDatabaseAsync(database);
    }

    public async Task<string> DatabaseAsync(bool migrate = true, string? target = null)
    {
        var name = "teapremium_test_p101_" + Guid.NewGuid().ToString("N");
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

    // Synthetic fixtures only. Intentionally no domain workflow or seed command.
    public const string Seed = """
INSERT INTO catalog.product_types(id,code,name) VALUES ('00000000-0000-0000-0000-000000000001','teapot','Fixture type');
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES
 ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','AM-XX-0001','fixture-pot','Fixture pot');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd) VALUES
 ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','AM-XX-0001',1000);
INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type) VALUES
 ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001','capacity','Fixture capacity','number');
INSERT INTO media.assets(id,storage_key,mime_type,byte_size) VALUES
 ('00000000-0000-0000-0000-000000000005','fixture-a','image/jpeg',1),
 ('00000000-0000-0000-0000-000000000006','fixture-b','image/png',1);
""";
    public const string ProductId = "00000000-0000-0000-0000-000000000002";
    public const string VariantId = "00000000-0000-0000-0000-000000000003";
}
