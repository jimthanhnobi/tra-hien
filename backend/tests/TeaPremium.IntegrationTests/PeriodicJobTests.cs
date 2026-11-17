using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class PeriodicJobTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task SameNamedJobRunsOnOnlyOneInstanceAndReleasesLockOnSuccess()
    {
        using var provider = CreateProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = Guid.NewGuid().ToString("N");
        var first = new DatabaseJob("P0_06_TestJob", provider.GetRequiredService<IServiceScopeFactory>(),
            Configuration(), async (db, ct) =>
        {
            db.Provinces.Add(new Province { Code = prefix + "a", Name = "first" });
            await db.SaveChangesAsync(ct);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        var run = first.RunOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRuns = 0;
        var second = new DatabaseJob("P0_06_TestJob", provider.GetRequiredService<IServiceScopeFactory>(),
            Configuration(), (db, _) =>
        {
            Interlocked.Increment(ref secondRuns);
            db.Provinces.Add(new Province { Code = prefix + "b", Name = "second" });
            return Task.CompletedTask;
        });
        Assert.False(await second.RunOnceAsync());
        Assert.Equal(0, secondRuns);

        release.SetResult();
        Assert.True(await run);
        Assert.True(await second.RunOnceAsync());
        Assert.Equal(1, secondRuns);
        await using var check = fixture.CreateContext();
        Assert.Equal(2, await check.Provinces.CountAsync(x => x.Code == prefix + "a" || x.Code == prefix + "b"));
    }

    [Fact]
    public async Task DifferentJobsHaveStableIndependentKeys()
    {
        Assert.Equal(PeriodicJob.AdvisoryLockKey("A"), PeriodicJob.AdvisoryLockKey("A"));
        Assert.NotEqual(PeriodicJob.AdvisoryLockKey("A"), PeriodicJob.AdvisoryLockKey("B"));

        using var provider = CreateProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateJob(provider, "P0_06_First", async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        var run = first.RunOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var other = CreateJob(provider, "P0_06_Second", _ => Task.CompletedTask);
        Assert.True(await other.RunOnceAsync());
        release.SetResult();
        Assert.True(await run);
    }

    [Fact]
    public async Task FailureAndCancellationBothReleaseLock()
    {
        using var provider = CreateProvider();
        var failing = CreateJob(provider, "P0_06_Cleanup", _ => throw new InvalidOperationException("secret failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunOnceAsync());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = CreateJob(provider, "P0_06_Cleanup", async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var cancellation = new CancellationTokenSource();
        var run = waiting.RunOnceAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var recovered = CreateJob(provider, "P0_06_Cleanup", _ => Task.CompletedTask);
        Assert.True(await recovered.RunOnceAsync());
    }

    [Fact]
    public async Task LostLockSessionCannotCommitStaleWorkAfterAnotherInstanceAcquires()
    {
        using var provider = CreateProvider();
        var prefix = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleWriteFailures = 0;
        var first = new DatabaseJob("P0_06_LostOwner", provider.GetRequiredService<IServiceScopeFactory>(),
            Configuration(), async (db, ct) =>
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                // Prove the supplied context is the session owning the advisory lock.
                await using var ownership = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND granted AND pid=pg_backend_pid()",
                    connection, (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
                Assert.Equal(1L, await ownership.ExecuteScalarAsync(ct));
                db.Provinces.Add(new Province { Code = prefix + "a", Name = "uncommitted before lock loss" });
                await db.SaveChangesAsync(ct);
                entered.SetResult(connection.ProcessID);
                await release.Task.WaitAsync(ct);
                db.Provinces.Add(new Province { Code = prefix + "stale", Name = "stale owner" });
                // G4 round 2: the third save used to reopen a session and auto-commit.
                // Catch all errors as a retrying callback would; every attempt must fail.
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try { await db.SaveChangesAsync(ct); }
                    catch (Exception) { staleWriteFailures++; }
                }
                Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO platform.provinces(code,name,is_active,sort_order) VALUES ({prefix + "raw"}, 'stale', true, 0)", ct));
            });
        var running = first.RunOnceAsync();
        var pid = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid, 5000)", admin);
            terminate.Parameters.Add("pid", NpgsqlDbType.Integer).Value = pid;
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
            var second = new DatabaseJob("P0_06_LostOwner", provider.GetRequiredService<IServiceScopeFactory>(),
                Configuration(), (db, _) =>
                {
                    db.Provinces.Add(new Province { Code = prefix + "b", Name = "new owner" });
                    return Task.CompletedTask;
                });
            Assert.True(await second.RunOnceAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => running);
        Assert.Equal(5, staleWriteFailures);
        await using var check = fixture.CreateContext();
        Assert.False(await check.Provinces.AnyAsync(x => x.Code == prefix + "a" || x.Code == prefix + "stale" || x.Code == prefix + "raw"));
        Assert.True(await check.Provinces.AnyAsync(x => x.Code == prefix + "b"));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task ProtectedTransactionAndPooledSessionAreCleanedUp(string outcome)
    {
        using var provider = CreateProvider();
        using var cancellation = new CancellationTokenSource();
        var code = Guid.NewGuid().ToString("N");
        // Disable the pool reset so a leaked lock cannot be hidden by DISCARD/RESET.
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MaxPoolSize = 1,
            NoResetOnClose = true,
            ApplicationName = code,
            Multiplexing = false
        }.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }).Build();
        var ownerPid = 0;
        var job = new DatabaseJob("P0_06_PooledCleanup", provider.GetRequiredService<IServiceScopeFactory>(),
            configuration, async (db, ct) =>
            {
                ownerPid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
                db.Provinces.Add(new Province { Code = code, Name = "protected" });
                await db.SaveChangesAsync(ct);
                if (outcome == "failure") throw new InvalidOperationException("test job failure");
                if (outcome == "cancellation") cancellation.Cancel();
            });
        if (outcome == "success") Assert.True(await job.RunOnceAsync(cancellation.Token));
        else if (outcome == "cancellation")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.RunOnceAsync(cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunOnceAsync(cancellation.Token));

        await using var check = fixture.CreateContext();
        Assert.Equal(outcome == "success", await check.Provinces.AnyAsync(x => x.Code == code));
        await using var pooled = new NpgsqlConnection(connectionString);
        await pooled.OpenAsync();
        Assert.Equal(ownerPid, pooled.ProcessID);
        await using var locks = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND granted AND pid=pg_backend_pid()", pooled);
        Assert.Equal(0L, await locks.ExecuteScalarAsync());
        await using var usable = new NpgsqlCommand("SELECT 1", pooled);
        Assert.Equal(1, await usable.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProtectedExecutionBoundaryDoesNotExposeAnIndependentScopedContext()
    {
        var method = typeof(PeriodicJob).GetMethod("ExecuteJobAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal([typeof(AppDbContext), typeof(CancellationToken)],
            method.GetParameters().Select(x => x.ParameterType).ToArray());
        using var provider = CreateProvider();
        using var unrelated = provider.CreateScope();
        var unrelatedDb = unrelated.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new DatabaseJob("P0_06_ExplicitBoundary", provider.GetRequiredService<IServiceScopeFactory>(),
            Configuration(), (db, _) =>
            {
                Assert.NotSame(unrelatedDb, db);
                Assert.NotSame(unrelatedDb.Database.GetDbConnection(), db.Database.GetDbConnection());
                Assert.NotNull(db.Database.CurrentTransaction);
                return Task.CompletedTask;
            });
        Assert.True(await job.RunOnceAsync());
    }

    private TestJob CreateJob(ServiceProvider provider, string name, Func<CancellationToken, Task> work)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build();
        return new TestJob(name, provider.GetRequiredService<IServiceScopeFactory>(), configuration, work);
    }

    private ServiceProvider CreateProvider() => new ServiceCollection()
        .AddPlatformPersistence(Configuration()).BuildServiceProvider();

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build();

    private sealed class TestJob(string name, IServiceScopeFactory scopes, IConfiguration configuration,
        Func<CancellationToken, Task> work)
        : PeriodicJob(name, TimeSpan.FromMinutes(1), scopes, configuration, TimeProvider.System, NullLogger.Instance)
    {
        protected override Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken)
            => work(cancellationToken);
    }

    private sealed class DatabaseJob(string name, IServiceScopeFactory scopes, IConfiguration configuration,
        Func<AppDbContext, CancellationToken, Task> work)
        : PeriodicJob(name, TimeSpan.FromMinutes(1), scopes, configuration, TimeProvider.System, NullLogger.Instance)
    {
        protected override Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken)
            => work(db, cancellationToken);
    }
}
