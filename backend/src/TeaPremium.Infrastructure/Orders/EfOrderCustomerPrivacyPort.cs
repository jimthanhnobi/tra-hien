using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;

namespace TeaPremium.Infrastructure.Orders;

// 02 §3 cross-module implementation owned by Orders/Sales for P2-12 privacy operations.
public sealed class EfOrderCustomerPrivacyPort(AppDbContext db) : IOrderCustomerPrivacyPort
{
    public Task<bool> HasOrdersAsync(Guid customerId, CancellationToken ct)
        => db.Set<PersistenceOrder>().AsNoTracking()
            .AnyAsync(x => x.CustomerId == customerId, ct);

    public async Task<int> ReassignCustomerOrdersAsync(Guid sourceCustomerId, Guid targetCustomerId, DateTime nowUtc, CancellationToken ct)
    {
        RequireTransaction();
        var orders = await db.Set<PersistenceOrder>()
            .FromSqlInterpolated($"SELECT *, xmin FROM sales.orders WHERE customer_id = {sourceCustomerId} ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        if (orders.Length == 0)
        {
            return 0;
        }

        foreach (var order in orders)
        {
            order.CustomerId = targetCustomerId;
            order.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(ct);
        return orders.Length;
    }

    public async Task<CustomerDeliveredOrderStats> ComputeDeliveredOrderStatsAsync(Guid customerId, CancellationToken ct)
    {
        var deliveredOrders = await db.Set<PersistenceOrder>().AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.Status == OrderStatus.Delivered)
            .Select(x => new DeliveredOrderStatsOrderInput(
                x.Id,
                x.SubtotalVnd,
                x.DiscountVnd,
                x.TotalVnd,
                x.DeliveredAt))
            .ToArrayAsync(ct);

        if (deliveredOrders.Length == 0)
        {
            return new CustomerDeliveredOrderStats(0, 0L, null, null);
        }

        var orderIds = deliveredOrders.Select(x => x.OrderId).ToArray();

        var orderLines = await db.Set<OrderLine>().AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .Select(x => new DeliveredOrderStatsLineInput(x.Id, x.OrderId, x.Quantity))
            .ToArrayAsync(ct);

        // O3b Phương án 3 (BR-ORD-07.2 / BR-RET-04): commercial refunds ONLY ('return', 'adjustment'); never 'overpayment'.
        var refunds = await db.Set<Refund>().AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId)
                && (x.Kind == RefundKind.Return || x.Kind == RefundKind.Adjustment))
            .Select(x => new DeliveredOrderStatsRefundInput(x.OrderId, x.Kind, x.AmountVnd, x.ShippingAmountVnd))
            .ToArrayAsync(ct);

        // BR-RET-04: count only resolved resolution='refund' return_requests (never replacement, never order_lines.returned_quantity).
        var returnLines = await (
            from rr in db.Set<ReturnRequest>().AsNoTracking()
            join rl in db.Set<ReturnLine>().AsNoTracking() on rr.Id equals rl.ReturnRequestId
            where orderIds.Contains(rr.OrderId)
                && rr.Status == ReturnRequestStatus.Resolved
                && rr.Resolution == ReturnResolution.Refund
            select new DeliveredOrderStatsReturnLineInput(
                rr.OrderId,
                rl.OrderLineId,
                rl.Quantity,
                rr.Status,
                rr.Resolution))
            .ToArrayAsync(ct);

        var computed = CustomerOrderStatsCalculator.Compute(deliveredOrders, orderLines, refunds, returnLines);
        return new CustomerDeliveredOrderStats(
            computed.DeliveredOrdersCount,
            computed.LifetimeValueVnd,
            computed.FirstDeliveredAt,
            computed.LastDeliveredAt);
    }

    public async Task<IReadOnlyList<string>> CheckEraseBlockersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        RequireTransaction();
        if (customerIds.Count == 0)
        {
            return [];
        }

        var distinctIds = customerIds.Distinct().ToArray();
        var orders = await db.Set<PersistenceOrder>()
            .FromSqlInterpolated($"SELECT *, xmin FROM sales.orders WHERE customer_id = ANY({distinctIds}) ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        if (orders.Length == 0)
        {
            return [];
        }

        var reasons = new List<string>(3);
        if (orders.Any(o => o.Status is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.Shipping))
        {
            reasons.Add("active_orders");
        }

        var orderIds = orders.Select(o => o.Id).ToArray();
        var hasOpenReturns = await db.Set<ReturnRequest>().AsNoTracking()
            .AnyAsync(rr => orderIds.Contains(rr.OrderId)
                && (rr.Status == ReturnRequestStatus.Requested || rr.Status == ReturnRequestStatus.Approved), ct);
        if (hasOpenReturns)
        {
            reasons.Add("open_return_requests");
        }

        var hasAwaitingSettlement = await db.Set<Payment>().AsNoTracking()
            .AnyAsync(p => orderIds.Contains(p.OrderId)
                && p.Status == PaymentRecordStatus.AwaitingSettlement, ct);
        if (hasAwaitingSettlement)
        {
            reasons.Add("awaiting_cod_settlement");
        }

        return reasons;
    }

    public async Task DeleteAccountCartsAsync(IReadOnlyCollection<Guid> customerAccountIds, CancellationToken ct)
    {
        RequireTransaction();
        if (customerAccountIds.Count == 0)
        {
            return;
        }

        var distinctAccountIds = customerAccountIds.Distinct().ToArray();
        var carts = await db.Set<Cart>()
            .Where(c => c.CustomerAccountId != null && distinctAccountIds.Contains(c.CustomerAccountId.Value))
            .ToArrayAsync(ct);

        if (carts.Length == 0)
        {
            return;
        }

        db.RemoveRange(carts);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> AnonymizeCustomerOrdersAsync(
        IReadOnlyCollection<Guid> customerIds,
        string anonymizedText,
        string anonymizedPhone,
        DateTime nowUtc,
        CancellationToken ct)
    {
        RequireTransaction();
        if (customerIds.Count == 0)
        {
            return 0;
        }

        var distinctIds = customerIds.Distinct().ToArray();
        var orders = await db.Set<PersistenceOrder>()
            .FromSqlInterpolated($"SELECT *, xmin FROM sales.orders WHERE customer_id = ANY({distinctIds}) ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        if (orders.Length == 0)
        {
            return 0;
        }

        // AC-P2-10 / BR-CRM-11: anonymize contact/shipping PII only; preserve all financial totals, status, location codes and lines.
        foreach (var order in orders)
        {
            order.ContactName = anonymizedText;
            order.ReceiverName = anonymizedText;
            order.ContactPhone = anonymizedPhone;
            order.ReceiverPhone = anonymizedPhone;
            order.ContactEmail = null;
            order.AddressLine = anonymizedText;
            order.CustomerNote = null;
            order.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(ct);
        return orders.Length;
    }

    public async Task<IReadOnlyList<CustomerExportOrderView>> ListOrdersForExportAsync(Guid customerId, CancellationToken ct)
    {
        var orders = await db.Set<PersistenceOrder>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.PlacedAt)
            .ThenByDescending(x => x.Id)
            .ToArrayAsync(ct);

        if (orders.Length == 0)
        {
            return [];
        }

        var orderIds = orders.Select(x => x.Id).ToArray();
        var lines = await db.Set<OrderLine>().AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .OrderBy(x => x.Id)
            .ToArrayAsync(ct);

        var linesByOrder = lines
            .GroupBy(x => x.OrderId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CustomerExportOrderLineView>)g.Select(l => new CustomerExportOrderLineView(
                    l.Id,
                    l.Sku,
                    l.ProductName,
                    l.VariantName,
                    l.Quantity,
                    l.ListPriceVnd,
                    l.UnitPriceVnd,
                    l.LineTotalVnd,
                    l.ReturnedQuantity)).ToArray());

        return orders.Select(o => new CustomerExportOrderView(
            o.Id,
            o.Code,
            o.Origin,
            o.SalesChannel,
            o.Status,
            o.PaymentMethod,
            o.PaymentStatus,
            o.ContactName,
            o.ContactPhone,
            o.ContactEmail,
            o.ReceiverName,
            o.ReceiverPhone,
            o.ProvinceCode,
            o.ProvinceName,
            o.WardCode,
            o.WardName,
            o.AddressLine,
            o.SubtotalVnd,
            o.DiscountVnd,
            o.ShippingFeeVnd,
            o.TotalVnd,
            o.CustomerNote,
            o.IsBackfilled,
            o.PlacedAt,
            o.ConfirmedAt,
            o.ShippedAt,
            o.DeliveredAt,
            o.FailedAt,
            o.CancelledAt,
            linesByOrder.TryGetValue(o.Id, out var orderLines) ? orderLines : [])).ToArray();
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Order customer privacy mutations require an active command transaction.");
        }
    }
}
