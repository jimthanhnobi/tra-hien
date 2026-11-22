using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace TeaPremium.IntegrationTests;

// Only disposable PostgreSQL 17 containers. No application permission rules here:
// roles/preflight come from infra; all object permissions come from migrations.
internal static class DatabaseProvisioning
{
    internal const string Password = "Disposable-Test-Only!";
    internal static string Artifacts => Path.Combine(AppContext.BaseDirectory, "Postgres");

    internal static PostgreSqlBuilder Builder() => new PostgreSqlBuilder("postgres:17")
        .WithEnvironment("TP_DATABASE", "teapremium_test")
        .WithEnvironment("TP_OWNER_PASSWORD", Password)
        .WithEnvironment("TP_APP_PASSWORD", Password)
        .WithEnvironment("TP_READONLY_PASSWORD", Password)
        .WithResourceMapping(Artifacts + "/", "/opt/teapremium/");

    internal static PostgreSqlBuilder FastBuilder() => Builder()
        .WithCommand(
            "-c", "fsync=off",
            "-c", "synchronous_commit=off",
            "-c", "full_page_writes=off");

    internal static async Task ProvisionAsync(PostgreSqlContainer container, string database)
    {
        var result = await container.ExecAsync(["env", "TP_DATABASE=" + database, "sh", "/opt/teapremium/init-roles.sh"]);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    internal static string Login(string adminConnection, string role, string? database = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnection) { Username = role, Password = Password };
        if (database is not null) builder.Database = database;
        return builder.ConnectionString;
    }

    internal static async Task MigrateAsync(string ownerConnection)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ownerConnection).Options);
        await db.Database.ExecuteSqlRawAsync(GrantsSql.Preflight);
        await db.Database.MigrateAsync();
    }
}
