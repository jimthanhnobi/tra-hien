using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Orders;

public sealed class EfCustomerOrderQueries(AppDbContext db) : ICustomerOrderQueries
{
    public async Task<CatalogPage<CustomerOrderSummaryView>> ListByCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Set<Order>().AsNoTracking()
            .Where(x => x.CustomerId == customerId);

        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.PlacedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new CustomerOrderSummaryView(
                x.Id,
                x.Code,
                x.Origin,
                x.SalesChannel,
                x.Status,
                x.PaymentMethod,
                x.PaymentStatus,
                x.SubtotalVnd,
                x.DiscountVnd,
                x.ShippingFeeVnd,
                x.TotalVnd,
                x.PlacedAt,
                x.DeliveredAt,
                x.CancelledAt))
            .ToArrayAsync(ct);

        return new CatalogPage<CustomerOrderSummaryView>(items, page, pageSize, total);
    }
}
