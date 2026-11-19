using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Media;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Media;

// P1-02 / 02 §5.5 / 03 §7. External deletes are idempotent and only affect terminal/expired uploads.
public sealed class UploadCleanup(AppDbContext db, IMediaStorage storage, IClock clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cutoff = clock.UtcNow.UtcDateTime.AddHours(-24);
        var sessions = await db.Set<UploadSession>().FromSqlInterpolated($"""
            SELECT * FROM media.upload_sessions WHERE created_at < {cutoff} AND status IN ('pending','failed','completed')
            ORDER BY created_at LIMIT 100 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var session in sessions)
        {
            await storage.DeleteStagingAsync(MediaKeys.Staging(session), ct);
            if (session.Status == "completed") db.Remove(session);
            else
            {
                // A suffix collision must never delete an asset belonging to another successful intent.
                if (!await db.Set<MediaAsset>().AnyAsync(x => x.StorageKey == session.StorageKey, ct))
                    await storage.DeleteAssetAsync(session.StorageKey, session.Purpose, ct);
                session.Status = "expired";
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return sessions.Count;
    }
}

public sealed class ExpireUploadSessions(IServiceScopeFactory scopes, TimeProvider time, ILogger<ExpireUploadSessions> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Error, new(1, "MediaCleanupFailed"), "Media cleanup failed; retry next interval.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                // Bounded batches; row locks coordinate multiple hosts and foreground complete calls.
                await scope.ServiceProvider.GetRequiredService<UploadCleanup>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { Failed(logger, null); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
