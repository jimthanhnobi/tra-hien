using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class OutboxAttemptLimitTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task PendingRowAlreadyAtTenAttemptsBecomesDeadWithoutDelivery()
    {
        var id = Guid.CreateVersion7();
        var now = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        await using (var seed = fixture.CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                Type = "Test",
                Payload = "{}",
                OccurredAt = now,
                AvailableAt = now,
                Attempts = 10,
                Status = "pending"
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var processor = new OutboxBatchProcessor(db, new FixedClock(), [new FailingHandler()]);
            Assert.Equal(1, await processor.ProcessAsync(1, CancellationToken.None));
        }

        await using var check = fixture.CreateContext();
        var message = await check.OutboxMessages.SingleAsync(x => x.Id == id);
        Assert.Equal(10, message.Attempts);
        Assert.Equal("dead", message.Status);
        Assert.Equal("attempts_exhausted", message.LastError);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingHandler : IOutboxHandler
    {
        public string MessageType => "Test";
        public Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("An eleventh delivery is forbidden.");
    }
}
