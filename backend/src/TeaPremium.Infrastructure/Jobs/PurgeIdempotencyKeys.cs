using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Jobs;

public sealed class PurgeIdempotencyKeys(IServiceScopeFactory scopeFactory,
    IConfiguration configuration, TimeProvider timeProvider, IClock clock, ILogger<PurgeIdempotencyKeys> logger)
    : PeriodicJob("PurgeIdempotencyKeys", TimeSpan.FromHours(1), scopeFactory, configuration, timeProvider, logger)
{
    protected override async Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.UtcDateTime - TimeSpan.FromHours(48);
        // PeriodicJob deliberately forbids bulk/raw commands. Keep its ownership guard
        // intact and use bounded tracked batches on the supplied transaction instead.
        while (true)
        {
            var expired = await db.IdempotencyKeys.Where(x => x.CreatedAt < cutoff)
                .OrderBy(x => x.CreatedAt).Take(500).ToListAsync(cancellationToken);
            if (expired.Count == 0) break;
            db.IdempotencyKeys.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}
