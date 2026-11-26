using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Notifications;

// GAP-01 1B + GAP-02 2A (24h window): real AsNoTracking LINQ queries across P2-01 + P1 + P0 tables.
// Compatible with JobExecutionGuard (CommandSource.LinqQuery only); never selects PII, artisan/product names, or costs.
public sealed class EfDailyDigestRead(AppDbContext db) : IDailyDigestRead
{
    public Task<DailyDigestSnapshot> ReadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        ReadFromContextAsync(db, nowUtc, cancellationToken);

    internal static async Task<DailyDigestSnapshot> ReadFromContextAsync(
        AppDbContext db,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var vietnamDate = BusinessCalendar.ToVietnamDate(nowUtc);
        var nowDateTime = nowUtc.UtcDateTime;
        var expiringCutoffUtc = nowDateTime.Add(NotificationContracts.OrderPaymentExpiringWindow);
        var nextDayStartUtc = BusinessCalendar.ToUtc(vietnamDate.AddDays(1), TimeOnly.MinValue).UtcDateTime;

        // 1. Pending confirmation orders (GAP-02 2A, BR-PAY-02 O3a=C): pending, non-backfilled, excluding awaiting-payment bank transfers (0-VND orders have no payment obligation).
        var pendingConfirmationOrders = await db.Set<Order>().AsNoTracking()
            .LongCountAsync(o =>
                o.Status == "pending"
                && !o.IsBackfilled
                && !(o.PaymentMethod == "bank_transfer"
                     && o.TotalVnd > 0
                     && (o.PaymentStatus == "unpaid" || o.PaymentStatus == "partially_paid")),
                cancellationToken);

        // 2. Expiring bank transfer payments (GAP-02 2A, 24h ASSUMPTION owner-approved 2026-11-23; BR-PAY-02 O3a=C excludes 0-VND orders):
        //    status = pending, bank_transfer, total_vnd > 0, unpaid, now < payment_due_at <= now + 24h.
        var expiringPaymentOrders = await db.Set<Order>().AsNoTracking()
            .LongCountAsync(o =>
                o.Status == "pending"
                && !o.IsBackfilled
                && o.PaymentMethod == "bank_transfer"
                && o.TotalVnd > 0
                && o.PaymentStatus == "unpaid"
                && o.PaymentDueAt != null
                && o.PaymentDueAt > nowDateTime
                && o.PaymentDueAt <= expiringCutoffUtc,
                cancellationToken);

        // 3. Unsettled COD payments (BR-PAY-05 / BR-ORD-07).
        var unsettledCodQuery = db.Set<Payment>().AsNoTracking()
            .Where(p => p.Status == "awaiting_settlement" && p.Method == "cod");
        var unsettledCodCount = await unsettledCodQuery.LongCountAsync(cancellationToken);
        var unsettledCodTotalVnd = unsettledCodCount == 0
            ? 0L
            : await unsettledCodQuery.SumAsync(p => (long?)p.AmountVnd, cancellationToken) ?? 0L;

        // 4. Customer care follow-ups due today or overdue (BR-CRM-06): follow_up_at < end of today VN, not done, active customer.
        var followUpsDueToday = await (
            from interaction in db.Set<Interaction>().AsNoTracking()
            join customer in db.Set<Customer>().AsNoTracking() on interaction.CustomerId equals customer.Id
            where interaction.FollowUpAt != null
                  && interaction.FollowUpDoneAt == null
                  && interaction.FollowUpAt < nextDayStartUtc
                  && customer.ContactStatus != "erased"
                  && customer.MergedIntoCustomerId == null
            select interaction.Id).LongCountAsync(cancellationToken);

        // 5. Current low-stock SKUs (BR-INV-11) & active reconciliation mismatches (BR-INV-13) — SKU/counts only, no names or costs.
        var lowStockQuery = from variant in db.Set<Variant>().AsNoTracking()
                            join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
                            from level in levels.DefaultIfEmpty()
                            let onHand = level == null ? 0 : level.OnHand
                            let reserved = level == null ? 0 : level.Reserved
                            let available = onHand - reserved
                            where variant.LowStockThreshold > 0 && available <= variant.LowStockThreshold
                            select new { variant.Id, variant.Sku, available, variant.LowStockThreshold };

        var lowStockTotal = await lowStockQuery.LongCountAsync(cancellationToken);
        var lowStockItems = await lowStockQuery
            .OrderBy(x => x.Sku)
            .ThenBy(x => x.Id)
            .Take(NotificationContracts.DailyDigestLowStockMaxItems)
            .Select(x => new DailyDigestLowStockItem(x.Sku, x.available, x.LowStockThreshold))
            .ToArrayAsync(cancellationToken);

        var reconciliationMismatchTotal = await (
            from variant in db.Set<Variant>().AsNoTracking()
            join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
            from level in levels.DefaultIfEmpty()
            let onHand = level == null ? 0 : level.OnHand
            let reserved = level == null ? 0 : level.Reserved
            let ledgerQty = db.Set<StockMovement>().Where(m => m.VariantId == variant.Id).Sum(m => (long?)m.Quantity) ?? 0L
            let activeReservedQty = db.Set<Reservation>().Where(r => r.VariantId == variant.Id && r.Status == "active").Sum(r => (long?)r.Quantity) ?? 0L
            where ledgerQty != (long)onHand || activeReservedQty != (long)reserved
            select variant.Id).LongCountAsync(cancellationToken);

        // 6. Dead outbox messages (BR-PLT-02 / BR-PLT-03).
        var deadOutboxCount = await db.OutboxMessages.AsNoTracking()
            .LongCountAsync(x => x.Status == "dead", cancellationToken);

        return new DailyDigestSnapshot(
            vietnamDate,
            pendingConfirmationOrders,
            expiringPaymentOrders,
            unsettledCodCount,
            unsettledCodTotalVnd,
            followUpsDueToday,
            lowStockTotal,
            lowStockItems,
            reconciliationMismatchTotal,
            deadOutboxCount);
    }
}
