namespace TeaPremium.Domain.Orders;

public sealed record ReturnLineRefundInput(Guid OrderLineId, int Quantity);

public sealed record OrderPaymentCalculation(
    string Status,
    long ConfirmedPaidVnd,
    long RefundedVnd,
    long OverpaymentRefundedVnd,
    long CommercialRefundedVnd,
    long EffectivePaidVnd,
    long NetPaidVnd,
    long TotalVnd,
    long RemainingToPayVnd,
    long OverpaidVnd,
    bool IsOverpaid,
    bool NeedsRefund);

public sealed record ReturnRefundCeilingResult(
    long CumulativeGoodsCeilingVnd,
    long CurrentReturnGoodsCeilingVnd,
    long RemainingMerchandiseCeilingVnd,
    long MaxGoodsRefundVnd,
    bool IsFullRefundReturnCompleted);

// BR-PAY-02 & BR-RET-02 pure domain calculator owned solely by P2-07 (Owner-approved 2026-11-26:
// O1=A, O2=A, O3a=C, O3b=Phương án 3) and shared across P2-06, P2-07, P2-08, P2-09 and P2-10.
public static class OrderPaymentStatusCalculator
{
    private static readonly IReadOnlyDictionary<Guid, int> EmptyPriorQuantities =
        new Dictionary<Guid, int>();

    public static OrderPaymentCalculation Calculate(
        long confirmedPaidVnd,
        long refundedVnd,
        long overpaymentRefundedVnd,
        long totalVnd,
        string orderStatus,
        bool isBackfilled)
    {
        if (confirmedPaidVnd < 0
            || refundedVnd < 0
            || overpaymentRefundedVnd < 0
            || overpaymentRefundedVnd > refundedVnd
            || totalVnd < 0
            || !OrderStatus.IsValid(orderStatus))
        {
            throw new DomainException("validation_failed", "Dữ liệu tính trạng thái thanh toán không hợp lệ.");
        }

        // Branch 1 (BR-ORD-09 / O3a=C): backfilled orders are immutable and never carry payment or refund ledger rows.
        if (isBackfilled)
        {
            if (confirmedPaidVnd != 0 || refundedVnd != 0)
                throw new DomainException("backfilled_order_immutable", "Đơn lịch sử backfill không được phát sinh thanh toán hoặc hoàn tiền.");

            return new OrderPaymentCalculation(
                OrderPaymentStatus.Unpaid,
                0L,
                0L,
                0L,
                0L,
                0L,
                0L,
                totalVnd,
                0L,
                0L,
                false,
                false);
        }

        var netPaidVnd = checked(confirmedPaidVnd - refundedVnd);
        if (netPaidVnd < 0)
            throw new DomainException("negative_net_payment", "Tổng tiền hoàn không được vượt tổng tiền đã thu xác nhận.");

        var effectivePaidVnd = checked(confirmedPaidVnd - overpaymentRefundedVnd);
        var commercialRefundedVnd = checked(refundedVnd - overpaymentRefundedVnd);

        // Branch 2 (O1=A / O3a=C): unsuccessful terminal orders keep refunded > 0 precedence; any net > 0 needs refund.
        if (orderStatus is OrderStatus.Cancelled or OrderStatus.DeliveryFailed)
        {
            string terminalStatus;
            if (refundedVnd > 0)
                terminalStatus = netPaidVnd == 0 ? OrderPaymentStatus.Refunded : OrderPaymentStatus.PartiallyRefunded;
            else if (confirmedPaidVnd == 0)
                terminalStatus = OrderPaymentStatus.Unpaid;
            else if (confirmedPaidVnd < totalVnd)
                terminalStatus = OrderPaymentStatus.PartiallyPaid;
            else
                terminalStatus = OrderPaymentStatus.Paid;

            return new OrderPaymentCalculation(
                terminalStatus,
                confirmedPaidVnd,
                refundedVnd,
                overpaymentRefundedVnd,
                commercialRefundedVnd,
                effectivePaidVnd,
                netPaidVnd,
                totalVnd,
                0L,
                0L,
                false,
                netPaidVnd > 0);
        }

        // Branch 3 (O2=A / O3a=C): active & delivered orders distinguish commercial refunds from overpayment refunds.
        // Zero-VND orders (total == 0, net == 0) settle naturally as 'paid' without synthetic payment rows.
        string activeStatus;
        if (commercialRefundedVnd > 0)
            activeStatus = netPaidVnd == 0 ? OrderPaymentStatus.Refunded : OrderPaymentStatus.PartiallyRefunded;
        else if (netPaidVnd >= totalVnd)
            activeStatus = OrderPaymentStatus.Paid;
        else if (netPaidVnd == 0)
            activeStatus = OrderPaymentStatus.Unpaid;
        else
            activeStatus = OrderPaymentStatus.PartiallyPaid;

        var remainingToPayVnd = effectivePaidVnd >= totalVnd ? 0L : checked(totalVnd - effectivePaidVnd);
        var overpaidVnd = effectivePaidVnd > totalVnd
            ? Math.Min(netPaidVnd, checked(effectivePaidVnd - totalVnd))
            : 0L;
        var isOverpaid = overpaidVnd > 0;

        return new OrderPaymentCalculation(
            activeStatus,
            confirmedPaidVnd,
            refundedVnd,
            overpaymentRefundedVnd,
            commercialRefundedVnd,
            effectivePaidVnd,
            netPaidVnd,
            totalVnd,
            remainingToPayVnd,
            overpaidVnd,
            isOverpaid,
            isOverpaid);
    }

    // BR-RET-02 (Owner-approved 2026-11-26: O3b Phương án 3):
    // - Uses cumulative half-up rounding at the final step via Int128 across prior refunded 'return' requests + current request.
    // - Validates per-line cumulative quantities against orderLine.Quantity (never relying on order_lines.returned_quantity
    //   and never counting 'replacement' requests).
    // - Caps goods refund by both cumulative return ceiling and remaining order merchandise after prior return + adjustment goods.
    // - Determines whether every line of the order (including 0-VND lines) has reached 100% refunded return quantity.
    public static ReturnRefundCeilingResult CalculateReturnRefundCeiling(
        Order order,
        IReadOnlyList<ReturnLineRefundInput> currentReturnLines,
        IReadOnlyDictionary<Guid, int>? priorRefundedQtyByOrderLine = null,
        long priorReturnGoodsRefundedVnd = 0L,
        long priorAdjustmentGoodsRefundedVnd = 0L)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(currentReturnLines);

        if (priorReturnGoodsRefundedVnd < 0 || priorAdjustmentGoodsRefundedVnd < 0)
            throw new DomainException("validation_failed", "Tổng tiền hoàn trước đó không hợp lệ.");

        if (currentReturnLines.Count == 0)
            throw new DomainException("return_request_has_no_lines", "Phiếu đổi trả hoàn tiền phải có ít nhất một dòng hàng.");

        var priorByLine = priorRefundedQtyByOrderLine ?? EmptyPriorQuantities;
        var orderLinesById = order.Lines.ToDictionary(x => x.Id);

        foreach (var (lineId, priorQty) in priorByLine)
        {
            if (!orderLinesById.TryGetValue(lineId, out var ol) || priorQty < 0 || priorQty > ol.Quantity)
                throw new DomainException("validation_failed", "Dữ liệu số lượng đã hoàn của dòng đơn hàng không hợp lệ.");
        }

        var currentByLine = new Dictionary<Guid, int>(currentReturnLines.Count);
        foreach (var line in currentReturnLines)
        {
            if (line.OrderLineId == Guid.Empty || !currentByLine.TryAdd(line.OrderLineId, line.Quantity))
                throw new DomainException("validation_failed", "Danh sách dòng đổi trả không hợp lệ hoặc bị trùng.");
            if (!orderLinesById.TryGetValue(line.OrderLineId, out var orderLine))
                throw new DomainException("validation_failed", "Dòng đổi trả không thuộc đơn hàng.");
            if (line.Quantity < 1)
                throw new DomainException("validation_failed", "Số lượng dòng đổi trả phải lớn hơn 0.");

            var priorQty = priorByLine.GetValueOrDefault(line.OrderLineId, 0);
            if (checked(priorQty + line.Quantity) > orderLine.Quantity)
                throw new DomainException("return_quantity_exceeds_order", "Tổng số lượng hoàn tiền của dòng hàng vượt quá số lượng đã mua.");
        }

        Int128 cumulativeGross = 0;
        var isFullRefundReturnCompleted = order.Lines.Count > 0;
        foreach (var orderLine in order.Lines)
        {
            var priorQty = priorByLine.GetValueOrDefault(orderLine.Id, 0);
            var currentQty = currentByLine.GetValueOrDefault(orderLine.Id, 0);
            var totalQty = checked(priorQty + currentQty);
            cumulativeGross += (Int128)totalQty * orderLine.UnitPriceVnd;
            if (totalQty != orderLine.Quantity)
                isFullRefundReturnCompleted = false;
        }

        var netMerchandiseVnd = checked(order.SubtotalVnd - order.DiscountVnd);
        long cumulativeGoodsCeilingVnd;
        if (order.SubtotalVnd == 0 || netMerchandiseVnd == 0)
        {
            cumulativeGoodsCeilingVnd = 0L;
        }
        else if (cumulativeGross >= order.SubtotalVnd)
        {
            cumulativeGoodsCeilingVnd = netMerchandiseVnd;
        }
        else
        {
            // Half-up rounding at the final step (04-business-rules.md:4): floor((2 * cumGross * netMerch + subtotal) / (2 * subtotal)).
            var rounded = (cumulativeGross * netMerchandiseVnd * 2 + order.SubtotalVnd) / ((Int128)order.SubtotalVnd * 2);
            cumulativeGoodsCeilingVnd = checked((long)rounded);
        }

        var currentReturnGoodsCeilingVnd = cumulativeGoodsCeilingVnd > priorReturnGoodsRefundedVnd
            ? checked(cumulativeGoodsCeilingVnd - priorReturnGoodsRefundedVnd)
            : 0L;
        var priorCommercialGoods = checked(priorReturnGoodsRefundedVnd + priorAdjustmentGoodsRefundedVnd);
        var remainingMerchandiseCeilingVnd = netMerchandiseVnd > priorCommercialGoods
            ? checked(netMerchandiseVnd - priorCommercialGoods)
            : 0L;
        var maxGoodsRefundVnd = Math.Min(currentReturnGoodsCeilingVnd, remainingMerchandiseCeilingVnd);

        return new ReturnRefundCeilingResult(
            cumulativeGoodsCeilingVnd,
            currentReturnGoodsCeilingVnd,
            remainingMerchandiseCeilingVnd,
            maxGoodsRefundVnd,
            isFullRefundReturnCompleted);
    }
}
