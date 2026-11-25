using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

// BR-INV-13: nightly ledger and active-reservation reconciliation at/after 01:30 Asia/Ho_Chi_Minh,
// once per Vietnam calendar date. Uses only guarded AppDbContext LINQ + tracked OutboxMessages writes
// and never auto-mutates stock_levels or reservations.
public sealed class ReconcileInventory(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IClock clock,
    ILogger<ReconcileInventory> logger)
    : PeriodicJob(JobName, PollInterval, scopeFactory, configuration, timeProvider, logger)
{
    public const string JobName = "ReconcileInventory";
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeOnly ScheduledVietnamTime = new(1, 30);

    private readonly Lock _gate = new();
    private DateOnly? _lastCompletedVietnamDate;
    private DateOnly? _pendingVietnamDate;

    protected override async Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        _pendingVietnamDate = null;
        var now = clock.UtcNow;
        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        if (now < BusinessCalendar.ToUtc(vietnamDate, ScheduledVietnamTime))
            return;

        lock (_gate)
        {
            if (_lastCompletedVietnamDate == vietnamDate)
                return;
        }

        var dayStartUtc = BusinessCalendar.ToUtc(vietnamDate, TimeOnly.MinValue).UtcDateTime;
        var nextDayStartUtc = BusinessCalendar.ToUtc(vietnamDate.AddDays(1), TimeOnly.MinValue).UtcDateTime;
        var todaysPayloads = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == InventoryAlert.Message && x.OccurredAt >= dayStartUtc && x.OccurredAt < nextDayStartUtc)
            .Select(x => x.Payload)
            .ToListAsync(cancellationToken);

        if (todaysPayloads.Any(p => InventoryAlert.TryParse(p, out var existing)
                && existing.Kind == InventoryAlert.ReconciliationMismatch
                && existing.VietnamDate == vietnamDate))
        {
            _pendingVietnamDate = vietnamDate;
            return;
        }

        var mismatchedVariantIds = await (
            from variant in db.Set<Variant>().AsNoTracking()
            join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
            from level in levels.DefaultIfEmpty()
            let onHand = level == null ? 0 : level.OnHand
            let reserved = level == null ? 0 : level.Reserved
            let ledgerQty = db.Set<StockMovement>().Where(m => m.VariantId == variant.Id).Sum(m => (long?)m.Quantity) ?? 0L
            let activeReservedQty = db.Set<Reservation>().Where(r => r.VariantId == variant.Id && r.Status == "active").Sum(r => (long?)r.Quantity) ?? 0L
            where ledgerQty != (long)onHand || activeReservedQty != (long)reserved
            orderby variant.Id
            select variant.Id).ToListAsync(cancellationToken);

        var occurredAt = now.UtcDateTime;
        foreach (var variantId in mismatchedVariantIds)
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                Type = InventoryAlert.Message,
                Payload = InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, variantId, vietnamDate)),
                OccurredAt = occurredAt,
                AvailableAt = occurredAt,
                Attempts = 0,
                Status = "pending"
            });
        }

        _pendingVietnamDate = vietnamDate;
    }

    protected override void OnJobCommitted()
    {
        if (_pendingVietnamDate is { } completed)
        {
            lock (_gate) _lastCompletedVietnamDate = completed;
            _pendingVietnamDate = null;
        }
    }
}
