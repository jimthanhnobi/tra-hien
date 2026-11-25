using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Domain.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

// BR-INV-11 / BR-INV-13 / BR-PLT-02: internal idempotent InventoryAlert consumer.
// Re-queries current persisted state without PII or cost fields; stale/resolved and duplicate
// deliveries complete as safe no-ops without requiring a Telegram handler.
public sealed partial class InventoryAlertHandler(AppDbContext db, ILogger<InventoryAlertHandler> logger) : IOutboxHandler
{
    public string MessageType => InventoryAlert.Message;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
    {
        if (!InventoryAlert.TryParse(payload, out var alert))
            throw new InvalidOperationException("Invalid inventory alert payload.");

        if (alert.Kind == InventoryAlert.LowStock)
        {
            var current = await (from variant in db.Set<Variant>().AsNoTracking()
                                 join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
                                 from level in levels.DefaultIfEmpty()
                                 where variant.Id == alert.VariantId
                                 select new
                                 {
                                     variant.Id,
                                     variant.ProductId,
                                     variant.Sku,
                                     variant.LowStockThreshold,
                                     OnHand = level == null ? 0 : level.OnHand,
                                     Reserved = level == null ? 0 : level.Reserved
                                 }).SingleOrDefaultAsync(cancellationToken);

            if (current is null) return;
            var available = current.OnHand - current.Reserved;
            if (!StockRules.IsLowStock(available, current.LowStockThreshold)) return;

            LogLowStockAlert(logger, messageId, current.Id, current.ProductId, current.Sku,
                available, current.OnHand, current.Reserved, current.LowStockThreshold);
            return;
        }

        var reconciled = await (from variant in db.Set<Variant>().AsNoTracking()
                                join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
                                from level in levels.DefaultIfEmpty()
                                where variant.Id == alert.VariantId
                                select new
                                {
                                    variant.Id,
                                    variant.ProductId,
                                    variant.Sku,
                                    OnHand = level == null ? 0 : level.OnHand,
                                    Reserved = level == null ? 0 : level.Reserved,
                                    LedgerQuantity = db.Set<StockMovement>().Where(m => m.VariantId == variant.Id).Sum(m => (long?)m.Quantity) ?? 0L,
                                    ActiveReservationQuantity = db.Set<Reservation>().Where(r => r.VariantId == variant.Id && r.Status == "active").Sum(r => (long?)r.Quantity) ?? 0L
                                }).SingleOrDefaultAsync(cancellationToken);

        if (reconciled is null
            || (reconciled.LedgerQuantity == reconciled.OnHand && reconciled.ActiveReservationQuantity == reconciled.Reserved))
            return;

        LogMismatchAlert(logger, messageId, reconciled.Id, reconciled.ProductId, reconciled.Sku,
            reconciled.OnHand, reconciled.LedgerQuantity, reconciled.LedgerQuantity - reconciled.OnHand,
            reconciled.Reserved, reconciled.ActiveReservationQuantity, reconciled.ActiveReservationQuantity - reconciled.Reserved);
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "InventoryLowStockAlert",
        Level = LogLevel.Warning,
        Message = "Inventory low stock alert: messageId={MessageId}, variantId={VariantId}, productId={ProductId}, sku={Sku}, available={Available}, onHand={OnHand}, reserved={Reserved}, lowStockThreshold={LowStockThreshold}")]
    private static partial void LogLowStockAlert(
        ILogger logger,
        Guid messageId,
        Guid variantId,
        Guid productId,
        string sku,
        int available,
        int onHand,
        int reserved,
        int lowStockThreshold);

    [LoggerMessage(
        EventId = 2,
        EventName = "InventoryReconciliationMismatchAlert",
        Level = LogLevel.Warning,
        Message = "Inventory reconciliation mismatch alert: messageId={MessageId}, variantId={VariantId}, productId={ProductId}, sku={Sku}, onHand={OnHand}, ledgerQuantity={LedgerQuantity}, delta={Delta}, reserved={Reserved}, activeReservationQuantity={ActiveReservationQuantity}, reservedDelta={ReservedDelta}")]
    private static partial void LogMismatchAlert(
        ILogger logger,
        Guid messageId,
        Guid variantId,
        Guid productId,
        string sku,
        int onHand,
        long ledgerQuantity,
        long delta,
        int reserved,
        long activeReservationQuantity,
        long reservedDelta);
}
