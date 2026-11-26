using System.Text.Json;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record OrderSettingsSnapshot(
    long ShippingFlatFeeVnd,
    long? ShippingFreeThresholdVnd,
    int BankTransferHoldHours,
    int MaxQuantityPerLineWeb,
    bool CodRequiresCall,
    int MaxStaffDiscountPercent,
    bool AutoConfirmOnFullPayment = true);

public interface IOrderPricingRead
{
    Task<OrderSettingsSnapshot> GetOrderSettingsAsync(CancellationToken ct);
}

public interface IOrderCodeGenerator
{
    string NextCode(DateOnly vietnamDate);
}

public sealed record OrderLoadResult(Order? Order, bool ConcurrencyConflict);

// R-P201-06: sales.order_status_history is strictly append-only; no update or delete methods exist on this port.
public interface IOrderRepository
{
    Task<OrderLoadResult> LoadForUpdateAsync(Guid orderId, uint? expectedVersion, CancellationToken ct);
    Task<bool> HasActiveOrderShipmentAsync(Guid orderId, CancellationToken ct);
    Task<bool> HasOrderShipmentAsync(Guid orderId, CancellationToken ct);
    Task<string?> InsertAsync(Order order, DateOnly vietnamDate, CancellationToken ct);
    Task SaveUpdatedOrderAsync(Order order, OrderLineDelta? lineDelta, CancellationToken ct);
}

// Extension point for P2-08 (BR-ORD-07.1 COD awaiting_settlement in the same transaction as MarkDelivered).
public interface IOrderDeliveredHook
{
    Task OnOrderDeliveredAsync(Order order, CancellationToken ct);
}

public sealed record OrderLineSummary(
    Guid Id,
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? VariantName,
    int Quantity,
    long ListPriceVnd,
    long UnitPriceVnd,
    string PriceSource,
    string? PriceOverrideReason,
    long LineTotalVnd,
    long? UnitCostVnd);

public sealed record OrderSummary(
    Guid Id,
    string Code,
    string Origin,
    string SalesChannel,
    Guid CustomerId,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    DateTimeOffset? PaymentDueAt,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    Guid? VoucherId,
    uint Version,
    IReadOnlyList<OrderLineSummary> Lines)
{
    public static OrderSummary From(Order order) => new(
        order.Id,
        order.Code,
        order.Origin,
        order.SalesChannel,
        order.CustomerId,
        order.Status,
        order.PaymentMethod,
        order.PaymentStatus,
        order.PaymentDueAt,
        order.SubtotalVnd,
        order.DiscountVnd,
        order.ShippingFeeVnd,
        order.TotalVnd,
        order.VoucherId,
        order.Version,
        order.Lines.Select(line => new OrderLineSummary(
            line.Id,
            line.VariantId,
            line.ProductId,
            line.Sku,
            line.ProductName,
            line.VariantName,
            line.Quantity,
            line.ListPriceVnd,
            line.UnitPriceVnd,
            line.PriceSource,
            line.PriceOverrideReason,
            line.LineTotalVnd,
            line.UnitCostVnd)).ToArray());
}

public static class OutOfStockErrorFormatting
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Error CreateError(string message, IReadOnlyList<OutOfStockDetail> items)
    {
        var map = items
            .OrderBy(x => x.VariantId)
            .ToDictionary(
                x => x.VariantId.ToString("D"),
                x => new[] { JsonSerializer.Serialize(new { variantId = x.VariantId, sku = x.Sku, available = x.Available }, JsonOptions) },
                StringComparer.Ordinal);
        return new Error("out_of_stock", message, ErrorType.Conflict, map);
    }

    public static IReadOnlyList<OutOfStockDetail> Parse(Error error)
    {
        if (error.ValidationErrors is null || error.ValidationErrors.Count == 0) return [];
        var list = new List<OutOfStockDetail>(error.ValidationErrors.Count);
        foreach (var entry in error.ValidationErrors.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (entry.Value.Length == 0) continue;
            var item = JsonSerializer.Deserialize<OutOfStockDetail>(entry.Value[0], JsonOptions);
            if (item is not null) list.Add(item);
        }
        return list;
    }
}
