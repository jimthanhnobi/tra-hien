using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Outbox;

/// <summary>One scoped batch; row locks remain held through handler execution and commit.</summary>
public sealed class OutboxBatchProcessor(AppDbContext db, IClock clock, IEnumerable<IOutboxHandler> handlers)
{
    public async Task<int> ProcessAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var processed = 0;
        try
        {
            for (var i = 0; i < batchSize; i++)
            {
                if (!await ProcessOneAsync(cancellationToken)) break;
                processed++;
            }

            return processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed savepoint/transaction/commit cannot safely be retried in this scope.
            // Provider exceptions can contain SQL, parameters and payloads, so do not attach them.
            throw new InvalidOperationException("Outbox transaction failed; retry state was not confirmed.");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.UtcNow.UtcDateTime;
        var selected = await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT * FROM platform.outbox_messages
            WHERE status = 'pending' AND available_at <= {now}
            ORDER BY available_at, id
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var message = selected.SingleOrDefault();
        if (message is null) return false;

        // A legacy or manually repaired pending row must not receive an eleventh delivery.
        if (message.Attempts >= 10)
        {
            message.Status = "dead";
            message.LastError = "attempts_exhausted";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return true;
        }

        // A handler may SaveChanges before an external call fails. The savepoint keeps
        // such database changes out of the durable retry transition.
        await transaction.CreateSavepointAsync("before_delivery", cancellationToken);
        string? error = null;
        try
        {
            var handler = handlers.SingleOrDefault(x => x.MessageType == message.Type);
            if (handler is null)
            {
                error = "handler_unavailable";
                throw new OutboxHandlerUnavailableException();
            }
            await handler.HandleAsync(message.Id, message.Payload, cancellationToken);
            // Flush before declaring delivery successful. A handler can return normally
            // while leaving invalid FK/CHECK/concurrency mutations pending in the context.
            // Both early saves and this flush remain inside the recovery savepoint.
            await db.SaveChangesAsync(cancellationToken);
            message.Attempts++;
            message.Status = "processed";
            message.ProcessedAt = clock.UtcNow.UtcDateTime;
            message.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception)
        {
            await transaction.RollbackToSavepointAsync("before_delivery", CancellationToken.None);
            db.ChangeTracker.Clear();
            message = await db.OutboxMessages.SingleAsync(x => x.Id == message.Id, cancellationToken);
            message.Attempts++;
            // Persist fixed codes only: exceptions and payloads may contain PII.
            message.LastError = error ?? "handler_failed";
            if (message.Attempts >= 10)
                message.Status = "dead";
            else
                message.AvailableAt = clock.UtcNow.UtcDateTime.AddMinutes(Math.Min(Math.Pow(2, message.Attempts), 360));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }

    private sealed class OutboxHandlerUnavailableException : Exception;
}
