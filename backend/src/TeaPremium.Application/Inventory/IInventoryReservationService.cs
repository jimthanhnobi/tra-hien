using TeaPremium.Domain.Orders;
using TeaPremium.Domain.Pricing;

namespace TeaPremium.Application.Inventory;

public sealed record LockedOrderVariant(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string ProductSlug,
    string? VariantName,
    bool ProductActive,
    bool VariantActive,
    long? PriceVnd,
    bool IsUniquePiece,
    int LowStockThreshold,
    int OnHand,
    int Reserved,
    long? AvgCostVnd,
    IReadOnlyList<ScheduledPrice> Promotions)
{
    public int Available => OnHand - Reserved;
}

public sealed record OrderReservationLine(
    Guid OrderLineId,
    Guid VariantId,
    Guid ProductId,
    string Sku,
    int Quantity);

public sealed record ReservationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<OutOfStockDetail> OutOfStockItems)
{
    public static ReservationResult Success() => new(true, null, null, []);

    public static ReservationResult Failure(
        string code,
        string message,
        IReadOnlyList<OutOfStockDetail>? outOfStockItems = null) =>
        new(false, code, message, outOfStockItems ?? []);
}

public sealed record ConsumeReservationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyDictionary<Guid, long?> UnitCostByLineId)
{
    public static ConsumeReservationResult Success(IReadOnlyDictionary<Guid, long?> unitCostByLineId) =>
        new(true, null, null, unitCostByLineId);

    public static ConsumeReservationResult Failure(string code, string message) =>
        new(false, code, message, new Dictionary<Guid, long?>());
}

public sealed record RestockLine(Guid VariantId, int Quantity, long? UnitCostVnd);

// BR-INV-03/04/09/11/12 + AC-P2-01/02/03 + BR-SEO-21.
// Lock order: Order (if existing) -> catalog.products (ORDER BY id) -> catalog.variants (ORDER BY id) -> inventory.stock_levels (ORDER BY variant_id).
public interface IInventoryReservationService
{
    Task<IReadOnlyDictionary<Guid, LockedOrderVariant>> LockVariantsForOrderAsync(
        IReadOnlyCollection<Guid> variantIds,
        DateTimeOffset evaluatedAt,
        CancellationToken ct);

    Task<ReservationResult> ReserveForOrderAsync(
        IReadOnlyList<OrderReservationLine> lines,
        CancellationToken ct);

    Task<ReservationResult> SyncOrderLinesAsync(
        Guid orderId,
        OrderLineDelta delta,
        Func<CancellationToken, Task> persistOrderAndLinesAsync,
        CancellationToken ct);

    Task ReleaseForOrderAsync(
        Guid orderId,
        string closeReason,
        CancellationToken ct);

    Task<ConsumeReservationResult> ConsumeForOrderAsync(
        Guid orderId,
        IReadOnlyList<OrderLineItem> lines,
        string actorType,
        Guid? actorId,
        CancellationToken ct);

    Task RestockReturnedAsync(
        Guid refId,
        string refType,
        IReadOnlyList<RestockLine> lines,
        string actorType,
        Guid? actorId,
        string? reason,
        CancellationToken ct);
}
