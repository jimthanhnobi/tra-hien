using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class OutboxTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private readonly TestClock _clock = new();

    [Fact]
    public async Task ApiHostDispatcherUsesConfiguredPostgresConnection()
    {
        await ResetAsync(); // P0-06
        await SeedAsync(1);
        var handler = new ProbeHandler((_, _, _) => Task.CompletedTask);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = fixture.ConnectionString,
                    ["OutboxDispatcher:PollInterval"] = "00:00:00.1"
                })).ConfigureServices(services => services.AddSingleton<IOutboxHandler>(handler)));
        using var client = factory.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var check = fixture.CreateContext();
            if (await check.OutboxMessages.AnyAsync(x => x.Status == "processed")) break;
            await Task.Delay(50);
        }

        await using var verified = fixture.CreateContext();
        Assert.Equal("processed", (await verified.OutboxMessages.SingleAsync()).Status);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task BusinessRowAndOutboxCommitTogether()
    {
        await ResetAsync(); // P0-06
        await using var db = fixture.CreateContext();
        var writer = new EfOutboxWriter(db, _clock);
        var key = Guid.NewGuid().ToString("N");
        var result = await new EfUnitOfWork(db).ExecuteAsync(_ =>
        {
            db.Provinces.Add(new Province { Code = key, Name = "test" });
            writer.Enqueue("Test", "{\"kind\":\"test\"}");
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using var check = fixture.CreateContext();
        Assert.True(await check.Provinces.AnyAsync(x => x.Code == key));
        var message = Assert.Single(await check.OutboxMessages.ToListAsync());
        Assert.Equal(7, (message.Id.ToByteArray()[7] >> 4) & 0x0f);
        Assert.Equal("pending", message.Status);
        Assert.Equal(_clock.UtcNow.UtcDateTime, message.OccurredAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessRowAndOutboxRollbackTogether(bool cancel)
    {
        await ResetAsync(); // P0-06
        await using var db = fixture.CreateContext();
        var writer = new EfOutboxWriter(db, _clock);
        var key = Guid.NewGuid().ToString("N");
        using var source = new CancellationTokenSource();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EfUnitOfWork(db).ExecuteAsync(async ct =>
            {
                db.Provinces.Add(new Province { Code = key, Name = "test" });
                writer.Enqueue("Test", "{}");
                await db.SaveChangesAsync(ct);
                source.Cancel();
                return Result.Success(1);
            }, source.Token));
        else
        {
            var result = await new EfUnitOfWork(db).ExecuteAsync(async ct =>
            {
                db.Provinces.Add(new Province { Code = key, Name = "test" });
                writer.Enqueue("Test", "{}");
                await db.SaveChangesAsync(ct);
                return Result.Failure<int>(Error.BusinessRule("test", "test"));
            }, source.Token);
            Assert.False(result.IsSuccess);
        }

        await using var check = fixture.CreateContext();
        Assert.False(await check.Provinces.AnyAsync(x => x.Code == key));
        Assert.Empty(await check.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task WriterRequiresTransactionAndValidJson()
    {
        await ResetAsync(); // P0-06
        await using var db = fixture.CreateContext();
        var writer = new EfOutboxWriter(db, _clock);
        Assert.Throws<InvalidOperationException>(() => writer.Enqueue("Test", "{}"));
        await using var tx = await db.Database.BeginTransactionAsync();
        var error = Assert.Throws<ArgumentException>(() => writer.Enqueue("Test", "{private"));
        Assert.DoesNotContain("private", error.Message);
        Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public async Task TwoDispatchersSkipLockedRowAndHandleDistinctMessages()
    {
        await ResetAsync(); // P0-06
        await SeedAsync(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandler = new ProbeHandler(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        var first = new OutboxBatchProcessor(firstDb, _clock, [firstHandler]).ProcessAsync(1, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondHandler = new ProbeHandler((_, _, _) => Task.CompletedTask);
        var secondCount = await new OutboxBatchProcessor(secondDb, _clock, [secondHandler]).ProcessAsync(1, CancellationToken.None);
        Assert.Equal(1, secondCount);
        Assert.NotEqual(firstHandler.Seen.Single(), secondHandler.Seen.Single());
        release.SetResult();
        Assert.Equal(1, await first);
        await using var check = fixture.CreateContext();
        Assert.Equal(2, await check.OutboxMessages.CountAsync(x => x.Status == "processed"));
    }

    [Fact]
    public async Task LockedOnlyRowIsSkipped()
    {
        await ResetAsync(); // P0-06
        await SeedAsync(1);
        await using var locker = fixture.CreateContext();
        await using var tx = await locker.Database.BeginTransactionAsync();
        var locked = await locker.OutboxMessages.FromSqlRaw("SELECT * FROM platform.outbox_messages FOR UPDATE").ToListAsync();
        Assert.Single(locked);
        await using var competitor = fixture.CreateContext();
        var count = await new OutboxBatchProcessor(competitor, _clock, [new ProbeHandler((_, _, _) => Task.CompletedTask)])
            .ProcessAsync(5, CancellationToken.None);
        Assert.Equal(0, count);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task RetryBackoffCapsAndDeadAtTenWithoutLeakingSecrets()
    {
        await ResetAsync(); // BR-PLT-02
        await SeedAsync(1);
        const string secret = "customer@example.test token-PRIVATE";
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await using var db = fixture.CreateContext();
            var handler = new ProbeHandler((_, _, _) => throw new InvalidOperationException(secret));
            Assert.Equal(1, await new OutboxBatchProcessor(db, _clock, [handler]).ProcessAsync(1, CancellationToken.None));
            await using var check = fixture.CreateContext();
            var message = Assert.Single(await check.OutboxMessages.ToListAsync());
            Assert.Equal(attempt, message.Attempts);
            Assert.Equal("handler_failed", message.LastError);
            Assert.DoesNotContain(secret, message.LastError);
            if (attempt == 10)
                Assert.Equal("dead", message.Status);
            else
            {
                Assert.Equal("pending", message.Status);
                var expected = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempt), 360));
                Assert.Equal(_clock.UtcNow.UtcDateTime + expected, message.AvailableAt);
                _clock.Advance(expected);
            }
        }
    }

    [Fact]
    public async Task HandlerDatabaseChangesRollbackWhenDeliveryFails()
    {
        await ResetAsync(); // P0-06
        await SeedAsync(1);
        var key = Guid.NewGuid().ToString("N");
        await using var db = fixture.CreateContext();
        var handler = new ProbeHandler(async (_, _, ct) =>
        {
            db.Provinces.Add(new Province { Code = key, Name = "partial" });
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("private information");
        });
        await new OutboxBatchProcessor(db, _clock, [handler]).ProcessAsync(1, CancellationToken.None);
        await using var check = fixture.CreateContext();
        Assert.False(await check.Provinces.AnyAsync(x => x.Code == key));
        Assert.Equal(1, (await check.OutboxMessages.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task CrashAfterSideEffectCanRedeliverSameIdSafely()
    {
        await ResetAsync(); // P0-06
        await SeedAsync(1);
        var effectIds = new HashSet<Guid>();
        using var source = new CancellationTokenSource();
        var handler = new ProbeHandler((id, _, _) =>
        {
            effectIds.Add(id); // idempotent external effect keyed by message ID
            source.Cancel();
            throw new OperationCanceledException(source.Token);
        });
        await using (var firstDb = fixture.CreateContext())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OutboxBatchProcessor(firstDb, _clock, [handler]).ProcessAsync(1, source.Token));

        await using (var pendingDb = fixture.CreateContext())
        {
            var pending = await pendingDb.OutboxMessages.SingleAsync();
            Assert.Equal("pending", pending.Status);
            Assert.Equal(0, pending.Attempts);
        }

        await using (var secondDb = fixture.CreateContext())
        {
            var redelivery = new ProbeHandler((id, _, _) =>
            {
                effectIds.Add(id);
                return Task.CompletedTask;
            });
            await new OutboxBatchProcessor(secondDb, _clock, [redelivery]).ProcessAsync(1, CancellationToken.None);
            Assert.Equal(handler.Seen.Single(), redelivery.Seen.Single());
        }
        Assert.Single(effectIds);
    }

    private async Task SeedAsync(int count)
    {
        await using var db = fixture.CreateContext();
        for (var i = 0; i < count; i++)
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                Type = "Test",
                Payload = "{}",
                OccurredAt = _clock.UtcNow.UtcDateTime,
                AvailableAt = _clock.UtcNow.UtcDateTime,
                Status = "pending"
            });
        await db.SaveChangesAsync();
    }

    private Task ResetAsync() => fixture.ExecuteAsync("DELETE FROM platform.outbox_messages");

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class ProbeHandler(Func<Guid, string, CancellationToken, Task> action) : IOutboxHandler
    {
        public string MessageType => "Test";
        public List<Guid> Seen { get; } = [];
        public Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
        {
            Seen.Add(messageId);
            return action(messageId, payload, cancellationToken);
        }
    }
}
