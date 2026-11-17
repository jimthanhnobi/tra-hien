using System.Text.Json;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Outbox;

public sealed class EfOutboxWriter(AppDbContext db, IClock clock) : IOutboxWriter
{
    public void Enqueue(string type, string payload, DateTimeOffset? availableAt = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Outbox writes require an active transaction.");
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Outbox payload must be valid JSON.", nameof(payload));
        }

        var now = clock.UtcNow;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = payload,
            OccurredAt = now.UtcDateTime,
            AvailableAt = (availableAt ?? now).UtcDateTime,
            Attempts = 0,
            Status = "pending"
        });
    }
}
