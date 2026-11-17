using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P0-06 / BR-PLT-02: deterministic handler persistence failures must not poison a batch forever.
public sealed class OutboxRecoveryTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string PrivateValue = "customer@example.test secret-PRIVATE";
    private readonly TestClock _clock = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPendingHandlerMutationRetriesAndNextMessageProgresses(bool saveEarly)
    {
        await ResetAsync();
        var poison = await SeedAsync("poison");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var valid = await SeedAsync("valid");
        var provinceCode = Guid.NewGuid().ToString("N");
        var wardCode = Guid.NewGuid().ToString("N");
        var successfulCalls = 0;
        await using (var db = fixture.CreateContext())
        {
            var handler = new Handler(async (id, _, ct) =>
            {
                if (id == poison)
                {
                    if (saveEarly)
                    {
                        db.Provinces.Add(new Province { Code = provinceCode, Name = "partial" });
                        await db.SaveChangesAsync(ct);
                    }
                    db.Wards.Add(new Ward { Code = wardCode, Name = PrivateValue, ProvinceCode = "missing-parent" });
                }
                else
                {
                    successfulCalls++;
                }
            });
            Assert.Equal(2, await new OutboxBatchProcessor(db, _clock, [handler]).ProcessAsync(20, CancellationToken.None));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using var check = fixture.CreateContext();
        var failed = await check.OutboxMessages.SingleAsync(x => x.Id == poison);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("pending", failed.Status);
        Assert.Null(failed.ProcessedAt);
        Assert.Equal(_clock.UtcNow.UtcDateTime.AddMinutes(2), failed.AvailableAt);
        Assert.Equal("handler_failed", failed.LastError);
        Assert.DoesNotContain(PrivateValue, failed.LastError);
        Assert.False(await check.Wards.AnyAsync(x => x.Code == wardCode));
        Assert.False(await check.Provinces.AnyAsync(x => x.Code == provinceCode));
        var processed = await check.OutboxMessages.SingleAsync(x => x.Id == valid);
        Assert.Equal("processed", processed.Status);
        Assert.NotNull(processed.ProcessedAt);
        Assert.Equal(1, successfulCalls);

        // Neither the future retry nor the already processed valid row is redelivered.
        await using var next = fixture.CreateContext();
        Assert.Equal(0, await new OutboxBatchProcessor(next, _clock,
            [new Handler((_, _, _) => throw new InvalidOperationException("Unexpected redelivery."))])
            .ProcessAsync(20, CancellationToken.None));
        Assert.Equal("processed", (await check.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == valid)).Status);
    }

    [Fact]
    public async Task InvalidPendingMutationBecomesDeadAtTenthFailure()
    {
        await ResetAsync();
        var id = await SeedAsync("poison");
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await using var db = fixture.CreateContext();
            var handler = new Handler((_, _, _) =>
            {
                db.Wards.Add(new Ward { Code = Guid.NewGuid().ToString("N"), Name = PrivateValue, ProvinceCode = "missing-parent" });
                return Task.CompletedTask;
            });
            Assert.Equal(1, await new OutboxBatchProcessor(db, _clock, [handler]).ProcessAsync(20, CancellationToken.None));
            await using var check = fixture.CreateContext();
            var failed = await check.OutboxMessages.SingleAsync(x => x.Id == id);
            Assert.Equal(attempt, failed.Attempts);
            Assert.Equal(attempt == 10 ? "dead" : "pending", failed.Status);
            Assert.Equal("handler_failed", failed.LastError);
            Assert.Null(failed.ProcessedAt);
            if (attempt < 10)
            {
                var backoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempt), 360));
                Assert.Equal(_clock.UtcNow.UtcDateTime.Add(backoff), failed.AvailableAt);
                _clock.Advance(backoff);
            }
        }

        await using var final = fixture.CreateContext();
        Assert.Equal(0, await new OutboxBatchProcessor(final, _clock,
            [new Handler((_, _, _) => throw new InvalidOperationException("Dead row must not run."))])
            .ProcessAsync(20, CancellationToken.None));
        Assert.False(await final.Wards.AnyAsync());
    }

    [Fact]
    public async Task SavepointRecoveryFailureIsSanitizedAndDoesNotClaimRetryPersisted()
    {
        await ResetAsync();
        var id = await SeedAsync("poison");
        var interceptor = new FailingSavepointInterceptor();
        await using (var db = fixture.CreateContext(interceptor))
        {
            var processor = new OutboxBatchProcessor(db, _clock,
                [new Handler((_, _, _) => throw new InvalidOperationException(PrivateValue))]);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(20, CancellationToken.None));
            Assert.Equal("Outbox transaction failed; retry state was not confirmed.", failure.Message);
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain(PrivateValue, failure.ToString());
            Assert.Empty(db.ChangeTracker.Entries());
        }
        Assert.Equal(1, interceptor.RollbackAttempts);
        await using var check = fixture.CreateContext();
        var message = await check.OutboxMessages.SingleAsync(x => x.Id == id);
        Assert.Equal("pending", message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.LastError);
    }

    [Fact]
    public async Task SessionLossDuringHandlerFailsSafelyWithoutFalseRetryState()
    {
        await ResetAsync();
        var id = await SeedAsync("poison");
        await using (var db = fixture.CreateContext())
        {
            var processor = new OutboxBatchProcessor(db, _clock, [new Handler(async (_, _, _) =>
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await fixture.ExecuteAsync($"SELECT pg_terminate_backend({connection.ProcessID});");
            })]);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(20, CancellationToken.None));
            Assert.Equal("Outbox transaction failed; retry state was not confirmed.", failure.Message);
            Assert.Null(failure.InnerException);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using var check = fixture.CreateContext();
        var message = await check.OutboxMessages.SingleAsync(x => x.Id == id);
        Assert.Equal("pending", message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.ProcessedAt);
    }

    private Task ResetAsync() => fixture.ExecuteAsync("DELETE FROM platform.outbox_messages");

    private async Task<Guid> SeedAsync(string payloadValue)
    {
        await using var db = fixture.CreateContext();
        var id = Guid.CreateVersion7();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = "Recovery",
            Payload = System.Text.Json.JsonSerializer.Serialize(new { value = payloadValue, secret = PrivateValue }),
            OccurredAt = _clock.UtcNow.UtcDateTime,
            AvailableAt = _clock.UtcNow.UtcDateTime,
            Status = "pending"
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan amount) => UtcNow += amount;
    }

    private sealed class Handler(Func<Guid, string, CancellationToken, Task> action) : IOutboxHandler
    {
        public string MessageType => "Recovery";
        public Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
            => action(messageId, payload, cancellationToken);
    }

    private sealed class FailingSavepointInterceptor : DbTransactionInterceptor
    {
        public int RollbackAttempts { get; private set; }
        public override ValueTask<InterceptionResult> RollingBackToSavepointAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            RollbackAttempts++;
            throw new InvalidOperationException(PrivateValue);
        }
    }
}
