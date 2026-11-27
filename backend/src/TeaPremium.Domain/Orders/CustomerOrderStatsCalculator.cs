namespace TeaPremium.Domain.Orders;

public sealed record DeliveredOrderStatsOrderInput(
    Guid OrderId,
    long SubtotalVnd,
    long DiscountVnd,
    long TotalVnd,
    DateTime? DeliveredAt);

public sealed record DeliveredOrderStatsLineInput(
    Guid OrderLineId,
    Guid OrderId,
    int Quantity);

public sealed record DeliveredOrderStatsRefundInput(
    Guid OrderId,
    string Kind,
    long AmountVnd,
    long ShippingAmountVnd);

public sealed record DeliveredOrderStatsReturnLineInput(
    Guid OrderId,
    Guid OrderLineId,
    int Quantity,
    string ReturnStatus,
    string? ReturnResolution);

public sealed record DeliveredOrderStatsResult(
    int DeliveredOrdersCount,
    long LifetimeValueVnd,
    DateTime? FirstDeliveredAt,
    DateTime? LastDeliveredAt);

// BR-ORD-07.2, BR-ORD-09, BR-RET-04 (Owner-approved 2026-11-26: O3b Phương án 3):
// - Counts only commercial refunds (kind IN ('return','adjustment')); never subtracts 'overpayment' or 'cancellation'.
// - Excludes a delivered order under BR-RET-04 ONLY when:
//   (1) every order line has reached 100% returned quantity across resolved return_requests(resolution='refund')
//       (never counting 'replacement' and never relying on order_lines.returned_quantity), AND
//   (2) commercial goods refunds (SUM(amount_vnd - shipping_amount_vnd) WHERE kind IN ('return','adjustment'))
//       >= (subtotal_vnd - discount_vnd).
// - Remaining delivered orders (including is_backfilled=true) contribute max(0, total_vnd - commercial_refunded_vnd).
public static class CustomerOrderStatsCalculator
{
    public static DeliveredOrderStatsResult Compute(
        IReadOnlyList<DeliveredOrderStatsOrderInput> deliveredOrders,
        IReadOnlyList<DeliveredOrderStatsLineInput> orderLines,
        IReadOnlyList<DeliveredOrderStatsRefundInput> refunds,
        IReadOnlyList<DeliveredOrderStatsReturnLineInput> returnLines)
    {
        ArgumentNullException.ThrowIfNull(deliveredOrders);
        ArgumentNullException.ThrowIfNull(orderLines);
        ArgumentNullException.ThrowIfNull(refunds);
        ArgumentNullException.ThrowIfNull(returnLines);

        if (deliveredOrders.Count == 0)
        {
            return new DeliveredOrderStatsResult(0, 0L, null, null);
        }

        var linesByOrder = new Dictionary<Guid, List<DeliveredOrderStatsLineInput>>();
        foreach (var line in orderLines)
        {
            if (!linesByOrder.TryGetValue(line.OrderId, out var list))
            {
                list = [];
                linesByOrder[line.OrderId] = list;
            }

            list.Add(line);
        }

        var commercialRefundTotalByOrder = new Dictionary<Guid, long>();
        var commercialGoodsRefundByOrder = new Dictionary<Guid, long>();
        foreach (var refund in refunds)
        {
            if (refund.Kind is not (RefundKind.Return or RefundKind.Adjustment))
            {
                continue;
            }

            if (refund.AmountVnd < 0 || refund.ShippingAmountVnd < 0 || refund.ShippingAmountVnd > refund.AmountVnd)
            {
                throw new DomainException("validation_failed", "Dữ liệu hoàn tiền không hợp lệ.");
            }

            var goodsRefundVnd = checked(refund.AmountVnd - refund.ShippingAmountVnd);
            commercialRefundTotalByOrder[refund.OrderId] = checked(
                commercialRefundTotalByOrder.GetValueOrDefault(refund.OrderId) + refund.AmountVnd);
            commercialGoodsRefundByOrder[refund.OrderId] = checked(
                commercialGoodsRefundByOrder.GetValueOrDefault(refund.OrderId) + goodsRefundVnd);
        }

        var refundedQtyByOrderLine = new Dictionary<(Guid OrderId, Guid OrderLineId), int>();
        foreach (var returnLine in returnLines)
        {
            if (!string.Equals(returnLine.ReturnStatus, ReturnRequestStatus.Resolved, StringComparison.Ordinal)
                || !string.Equals(returnLine.ReturnResolution, ReturnResolution.Refund, StringComparison.Ordinal))
            {
                continue;
            }

            if (returnLine.Quantity <= 0)
            {
                continue;
            }

            var key = (returnLine.OrderId, returnLine.OrderLineId);
            refundedQtyByOrderLine[key] = checked(refundedQtyByOrderLine.GetValueOrDefault(key) + returnLine.Quantity);
        }

        var deliveredCount = 0;
        var lifetimeValueVnd = 0L;
        DateTime? firstDeliveredAt = null;
        DateTime? lastDeliveredAt = null;

        foreach (var order in deliveredOrders)
        {
            if (order.SubtotalVnd < 0 || order.DiscountVnd < 0 || order.DiscountVnd > order.SubtotalVnd || order.TotalVnd < 0)
            {
                throw new DomainException("validation_failed", "Dữ liệu số tiền đơn hàng không hợp lệ.");
            }

            var commercialRefundedVnd = commercialRefundTotalByOrder.GetValueOrDefault(order.OrderId);
            var commercialGoodsRefundedVnd = commercialGoodsRefundByOrder.GetValueOrDefault(order.OrderId);
            var netMerchandiseVnd = checked(order.SubtotalVnd - order.DiscountVnd);

            var allLinesReturnedViaRefund = false;
            if (linesByOrder.TryGetValue(order.OrderId, out var lines) && lines.Count > 0)
            {
                allLinesReturnedViaRefund = true;
                foreach (var line in lines)
                {
                    var refundedQty = refundedQtyByOrderLine.GetValueOrDefault((order.OrderId, line.OrderLineId));
                    if (refundedQty < line.Quantity)
                    {
                        allLinesReturnedViaRefund = false;
                        break;
                    }
                }
            }

            if (allLinesReturnedViaRefund && commercialGoodsRefundedVnd >= netMerchandiseVnd)
            {
                continue;
            }

            deliveredCount = checked(deliveredCount + 1);
            var orderContribution = order.TotalVnd > commercialRefundedVnd
                ? checked(order.TotalVnd - commercialRefundedVnd)
                : 0L;
            lifetimeValueVnd = checked(lifetimeValueVnd + orderContribution);

            if (order.DeliveredAt is { } deliveredAt)
            {
                if (firstDeliveredAt is null || deliveredAt < firstDeliveredAt.Value)
                {
                    firstDeliveredAt = deliveredAt;
                }

                if (lastDeliveredAt is null || deliveredAt > lastDeliveredAt.Value)
                {
                    lastDeliveredAt = deliveredAt;
                }
            }
        }

        return new DeliveredOrderStatsResult(deliveredCount, lifetimeValueVnd, firstDeliveredAt, lastDeliveredAt);
    }
}
