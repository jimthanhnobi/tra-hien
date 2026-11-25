using TeaPremium.Application.Catalog;

namespace TeaPremium.Application.Orders.Contracts;

public sealed record CustomerOrderSummaryView(
    Guid Id,
    string Code,
    string Origin,
    string SalesChannel,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    DateTime PlacedAt,
    DateTime? DeliveredAt,
    DateTime? CancelledAt);

public interface ICustomerOrderQueries
{
    Task<CatalogPage<CustomerOrderSummaryView>> ListByCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken ct);
}
