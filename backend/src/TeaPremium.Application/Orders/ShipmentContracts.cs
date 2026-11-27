using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record ShipmentEventView(
    Guid Id,
    string Status,
    string? Note,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? CreatedBy);

// Intentionally excludes unitCostVnd / avgCostVnd / grossProfitVnd (safe for both sales and owner per 02-architecture.md:235).
public sealed record ShipmentDetailView(
    Guid Id,
    Guid OrderId,
    string OrderCode,
    string OrderStatus,
    string OrderRowVersion,
    string Purpose,
    Guid? ReturnRequestId,
    string Provider,
    string CarrierName,
    string? TrackingCode,
    string Status,
    long CodAmountVnd,
    long? ShippingCostVnd,
    long? ReturnCostVnd,
    string? Note,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PickedUpAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? ReturnedAt,
    IReadOnlyList<ShipmentEventView> Events)
{
    public static ShipmentDetailView From(Shipment shipment, Order order) => new(
        shipment.Id,
        shipment.OrderId,
        order.Code,
        order.Status,
        CatalogVersions.Encode(order.Version),
        shipment.Purpose,
        shipment.ReturnRequestId,
        shipment.Provider,
        shipment.CarrierName,
        shipment.TrackingCode,
        shipment.Status,
        shipment.CodAmountVnd,
        shipment.ShippingCostVnd,
        shipment.ReturnCostVnd,
        shipment.Note,
        shipment.CreatedBy,
        shipment.CreatedAt,
        shipment.PickedUpAt,
        shipment.DeliveredAt,
        shipment.FailedAt,
        shipment.ReturnedAt,
        shipment.Events
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new ShipmentEventView(
                e.Id,
                e.Status,
                e.Note,
                e.Source,
                e.OccurredAt,
                e.CreatedBy))
            .ToArray());
}

public sealed record PatchShipmentChanges(
    bool HasTrackingCode,
    string? TrackingCode,
    bool HasShippingCostVnd,
    long? ShippingCostVnd,
    bool HasReturnCostVnd,
    long? ReturnCostVnd);

public sealed record ShipmentWriteResult(bool IsSuccess, string? ConflictCode)
{
    public static ShipmentWriteResult Success() => new(true, null);
    public static ShipmentWriteResult Conflict(string code) => new(false, code);
}

public sealed record ShipmentOrderLookup(Guid ShipmentId, Guid OrderId, string Purpose);

public interface IShipmentRepository
{
    Task<ShipmentOrderLookup?> FindOrderLookupAsync(Guid shipmentId, CancellationToken ct);
    Task<Shipment?> LoadForUpdateAsync(Guid shipmentId, CancellationToken ct);
    Task<Shipment?> FindDeliveredOrderShipmentAsync(Guid orderId, CancellationToken ct);
    Task<ShipmentWriteResult> InsertAsync(Shipment shipment, CancellationToken ct);
    Task<ShipmentWriteResult> SaveUpdatedAsync(Shipment shipment, CancellationToken ct);
    Task<bool> HasOrderReturnRestockMovementAsync(Guid orderId, CancellationToken ct);
    Task<bool> HasNonVoidedCodPaymentAsync(Guid orderId, CancellationToken ct);
    Task InsertAwaitingCodPaymentAsync(
        Guid id,
        Guid orderId,
        long amountVnd,
        Guid? createdBy,
        DateTimeOffset createdAt,
        CancellationToken ct);
}

// Read port for P2-06 (GET /api/v1/admin/orders/{id}).
public interface IOrderShipmentQueries
{
    Task<IReadOnlyList<ShipmentDetailView>> ListByOrderIdAsync(Guid orderId, CancellationToken ct);
}

// Seam G30 for P2-10: handles events on purpose = 'replacement' shipments without mutating the original Order.
public interface IReplacementShipmentHook
{
    Task OnReplacementShipmentEventAsync(Shipment shipment, ShipmentEventEntry entry, CancellationToken ct);
}
