using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Outbox;

public sealed class EfOutboxWriter(AppDbContext db, IClock clock) : IOutboxWriter
{
    private readonly Dictionary<string, OutboxMessage> _deduplicated = new(StringComparer.Ordinal);
    private Guid? _dedupTransactionId;

    public void Enqueue(string type, string payload, DateTimeOffset? availableAt = null) =>
        EnqueueCore(type, payload, null, availableAt);

    public void Enqueue(string type, string payload, string deduplicationKey, DateTimeOffset? availableAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        EnqueueCore(type, payload, deduplicationKey, availableAt);
    }

    public void ClearDeduplicated(string deduplicationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        var transaction = db.Database.CurrentTransaction;
        if (transaction is null || _dedupTransactionId != transaction.TransactionId) return;
        if (!_deduplicated.Remove(deduplicationKey, out var existing)) return;

        var entry = db.Entry(existing);
        if (entry.State == EntityState.Added)
            entry.State = EntityState.Detached;
        else if (entry.State != EntityState.Detached)
            db.OutboxMessages.Remove(existing);
    }

    private void EnqueueCore(string type, string payload, string? deduplicationKey, DateTimeOffset? availableAt)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Outbox writes require an active transaction.");
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
        var occurredAt = now.UtcDateTime;
        var available = (availableAt ?? now).UtcDateTime;

        if (deduplicationKey is not null)
        {
            if (_dedupTransactionId != transaction.TransactionId)
            {
                _dedupTransactionId = transaction.TransactionId;
                _deduplicated.Clear();
            }

            if (_deduplicated.TryGetValue(deduplicationKey, out var existing))
            {
                existing.Type = type;
                existing.Payload = payload;
                existing.OccurredAt = occurredAt;
                existing.AvailableAt = available;
                if (db.Entry(existing).State == EntityState.Detached)
                    db.OutboxMessages.Add(existing);
                return;
            }
        }

        var message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
            AvailableAt = available,
            Attempts = 0,
            Status = "pending"
        };
        db.OutboxMessages.Add(message);
        if (deduplicationKey is not null)
            _deduplicated[deduplicationKey] = message;
    }
}
