using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P0-06 / architecture 5.2, 5.5: a protected execution can never gain a second session/transaction.
public sealed class PeriodicJobOwnershipTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Theory]
    [InlineData("close_reopen")]
    [InlineData("replacement_transaction")]
    [InlineData("reuse_transaction")]
    [InlineData("commit_transaction")]
    [InlineData("raw_sql_async")]
    [InlineData("raw_sql_sync")]
    [InlineData("raw_query")]
    [InlineData("execute_update")]
    [InlineData("execute_delete")]
    public async Task UnsupportedOperationsPermanentlyRevokeWritesEvenIfCallbackSwallowsErrors(string operation)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build();
        using var provider = new ServiceCollection().AddPlatformPersistence(configuration).BuildServiceProvider();
        var code = Guid.NewGuid().ToString("N");
        var attempts = 0;
        using var job = new TestJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, async (db, ct) =>
        {
            db.Provinces.Add(new Province { Code = code, Name = "must roll back" });
            await db.SaveChangesAsync(ct);
            switch (operation)
            {
                case "close_reopen":
                    // Simulate unexpected close of the actual PostgreSQL session.
                    await db.Database.GetDbConnection().CloseAsync();
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.OpenConnectionAsync(ct));
                    break;
                case "replacement_transaction":
                    await db.Database.CurrentTransaction!.DisposeAsync();
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.BeginTransactionAsync(ct));
                    break;
                case "reuse_transaction":
                    var original = db.Database.CurrentTransaction!.GetDbTransaction();
                    await db.Database.CurrentTransaction!.DisposeAsync();
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.UseTransactionAsync(original, ct));
                    break;
                case "commit_transaction":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.CommitTransactionAsync(ct));
                    break;
                case "raw_sql_async":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO platform.provinces(code,name) VALUES ({code + "raw"}, 'private')", ct));
                    break;
                case "raw_sql_sync":
                    Assert.Throws<InvalidOperationException>(() => db.Database.ExecuteSqlInterpolated(
                        $"INSERT INTO platform.provinces(code,name) VALUES ({code + "raw"}, 'private')"));
                    break;
                case "raw_query":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Provinces
                        .FromSqlRaw("SELECT * FROM platform.provinces").ToListAsync(ct));
                    break;
                case "execute_update":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Provinces.Where(x => x.Code == code)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "unsupported"), ct));
                    break;
                case "execute_delete":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => db.Provinces.Where(x => x.Code == code)
                        .ExecuteDeleteAsync(ct));
                    break;
            }

            // Neither a caught exception nor a replacement EF transaction may restore Active.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
                attempts++;
            }
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        await Assert.ThrowsAnyAsync<Exception>(() => job.RunOnceAsync());
        Assert.Equal(5, attempts);
        await using var check = fixture.CreateContext();
        Assert.False(await check.Provinces.AnyAsync(x => x.Code == code || x.Code == code + "raw"));
    }

    [Fact]
    public async Task GuardedTrackedWritesAndLinqCommandsCommitOnOriginalTransaction()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build();
        using var provider = new ServiceCollection().AddPlatformPersistence(configuration).BuildServiceProvider();
        var code = Guid.NewGuid().ToString("N");
        using var job = new TestJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, async (db, ct) =>
        {
            var session = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            db.Provinces.Add(new Province { Code = code, Name = "first" });
            await db.SaveChangesAsync(ct);
            var province = await db.Provinces.SingleAsync(x => x.Code == code, ct);
            province.Name = "updated";
            await db.SaveChangesAsync(ct);
            Assert.Equal("updated", await db.Provinces.AsNoTracking().Where(x => x.Code == code).Select(x => x.Name).SingleAsync(ct));
            Assert.Equal(session, ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
        });
        Assert.True(await job.RunOnceAsync());
        await using var check = fixture.CreateContext();
        Assert.Equal("updated", await check.Provinces.Where(x => x.Code == code).Select(x => x.Name).SingleAsync());
    }

    private sealed class TestJob(IServiceScopeFactory scopes, IConfiguration configuration,
        Func<AppDbContext, CancellationToken, Task> work)
        : PeriodicJob("P0_06_OwnershipBoundary", TimeSpan.FromMinutes(1), scopes, configuration,
            TimeProvider.System, NullLogger.Instance)
    {
        protected override Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken) => work(db, cancellationToken);
    }
}
