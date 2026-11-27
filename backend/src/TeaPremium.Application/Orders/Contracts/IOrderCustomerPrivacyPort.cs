namespace TeaPremium.Application.Orders.Contracts;

public sealed record CustomerDeliveredOrderStats(
    int DeliveredOrdersCount,
    long LifetimeValueVnd,
    DateTime? FirstDeliveredAt,
    DateTime? LastDeliveredAt);

// BR-CRM-13 (P2-12): export views strictly exclude unitCostVnd, avgCostVnd, grossProfitVnd, priceOverrideReason and internalNote.
public sealed record CustomerExportOrderLineView(
    Guid Id,
    string Sku,
    string ProductName,
    string? VariantName,
    int Quantity,
    long ListPriceVnd,
    long UnitPriceVnd,
    long LineTotalVnd,
    int ReturnedQuantity);

public sealed record CustomerExportOrderView(
    Guid Id,
    string Code,
    string Origin,
    string SalesChannel,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    string ContactName,
    string ContactPhone,
    string? ContactEmail,
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string ProvinceName,
    string? WardCode,
    string WardName,
    string AddressLine,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    string? CustomerNote,
    bool IsBackfilled,
    DateTime PlacedAt,
    DateTime? ConfirmedAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    DateTime? FailedAt,
    DateTime? CancelledAt,
    IReadOnlyList<CustomerExportOrderLineView> Lines);

// 02 §3 cross-module contract owned by Orders/Sales for P2-12 customer privacy operations.
public interface IOrderCustomerPrivacyPort
{
    Task<bool> HasOrdersAsync(Guid customerId, CancellationToken ct);
    Task<int> ReassignCustomerOrdersAsync(Guid sourceCustomerId, Guid targetCustomerId, DateTime nowUtc, CancellationToken ct);
    Task<CustomerDeliveredOrderStats> ComputeDeliveredOrderStatsAsync(Guid customerId, CancellationToken ct);
    Task<IReadOnlyList<string>> CheckEraseBlockersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct);
    Task DeleteAccountCartsAsync(IReadOnlyCollection<Guid> customerAccountIds, CancellationToken ct);
    Task<int> AnonymizeCustomerOrdersAsync(
        IReadOnlyCollection<Guid> customerIds,
        string anonymizedText,
        string anonymizedPhone,
        DateTime nowUtc,
        CancellationToken ct);
    Task<IReadOnlyList<CustomerExportOrderView>> ListOrdersForExportAsync(Guid customerId, CancellationToken ct);
}
