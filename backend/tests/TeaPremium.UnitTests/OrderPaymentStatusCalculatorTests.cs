using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.UnitTests;

public sealed class OrderPaymentStatusCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly OrderContactSnapshot Contact = new(
        "Nguyen Van A", "+84901234567", "buyer@example.invalid",
        "Nguyen Van A", "+84901234567", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "10 Duong Thu Nghiem");
    private static readonly OrderAttributionSnapshot Attribution = new(OrderAcquisitionSource.Direct, true);

    [Theory]
    [InlineData(0L, 0L, 0L, 500_000L, OrderPaymentStatus.Unpaid, 0L, 0L, 500_000L, 0L)]
    [InlineData(0L, 0L, 0L, 0L, OrderPaymentStatus.Paid, 0L, 0L, 0L, 0L)]
    [InlineData(200_000L, 0L, 0L, 500_000L, OrderPaymentStatus.PartiallyPaid, 200_000L, 200_000L, 300_000L, 0L)]
    [InlineData(499_999L, 0L, 0L, 500_000L, OrderPaymentStatus.PartiallyPaid, 499_999L, 499_999L, 1L, 0L)]
    [InlineData(500_000L, 0L, 0L, 500_000L, OrderPaymentStatus.Paid, 500_000L, 500_000L, 0L, 0L)]
    [InlineData(700_000L, 0L, 0L, 500_000L, OrderPaymentStatus.Paid, 700_000L, 700_000L, 0L, 200_000L)]
    // O3a=C: overpayment refund back to exact total restores 'paid', Remaining=0, Overpaid=0, NeedsRefund=false.
    [InlineData(700_000L, 200_000L, 200_000L, 500_000L, OrderPaymentStatus.Paid, 500_000L, 500_000L, 0L, 0L)]
    // Overpayment partial refund still leaves overpaid remainder.
    [InlineData(700_000L, 120_000L, 120_000L, 500_000L, OrderPaymentStatus.Paid, 580_000L, 580_000L, 0L, 80_000L)]
    // Commercial refund on fully paid order: status partially_refunded, customer owes 0 (Remaining = max(0, total - effectivePaid)).
    [InlineData(500_000L, 100_000L, 0L, 500_000L, OrderPaymentStatus.PartiallyRefunded, 500_000L, 400_000L, 0L, 0L)]
    [InlineData(500_000L, 500_000L, 0L, 500_000L, OrderPaymentStatus.Refunded, 500_000L, 0L, 0L, 0L)]
    // Commercial refund FIRST on overpaid order (700k paid on 500k, return 500k) -> net=200k, overpaid=min(200k, 700k-500k)=200k!
    [InlineData(700_000L, 500_000L, 0L, 500_000L, OrderPaymentStatus.PartiallyRefunded, 700_000L, 200_000L, 0L, 200_000L)]
    // Then overpayment refund 200k -> refunded, net=0, overpaid=0, remaining=0!
    [InlineData(700_000L, 700_000L, 200_000L, 500_000L, OrderPaymentStatus.Refunded, 500_000L, 0L, 0L, 0L)]
    // 0-VND order with accidental payment 100k -> paid + overpaid 100k.
    [InlineData(100_000L, 0L, 0L, 0L, OrderPaymentStatus.Paid, 100_000L, 100_000L, 0L, 100_000L)]
    // Order total raised after overpayment refund (paid 700k, overpayment refunded 200k -> effectivePaid 500k, new total 800k) -> partially_paid, remaining 300k.
    [InlineData(700_000L, 200_000L, 200_000L, 800_000L, OrderPaymentStatus.PartiallyPaid, 500_000L, 500_000L, 300_000L, 0L)]
    public void BrPay02ActiveAndDeliveredCombinationsFollowO3aCPrecedence(
        long paid,
        long refunded,
        long overpaymentRefunded,
        long total,
        string expectedStatus,
        long expectedEffectivePaid,
        long expectedNet,
        long expectedRemaining,
        long expectedOverpaid)
    {
        var calc = OrderPaymentStatusCalculator.Calculate(paid, refunded, overpaymentRefunded, total, OrderStatus.Confirmed, isBackfilled: false);

        Assert.Equal(expectedStatus, calc.Status);
        Assert.Equal(paid, calc.ConfirmedPaidVnd);
        Assert.Equal(refunded, calc.RefundedVnd);
        Assert.Equal(overpaymentRefunded, calc.OverpaymentRefundedVnd);
        Assert.Equal(refunded - overpaymentRefunded, calc.CommercialRefundedVnd);
        Assert.Equal(expectedEffectivePaid, calc.EffectivePaidVnd);
        Assert.Equal(expectedNet, calc.NetPaidVnd);
        Assert.Equal(total, calc.TotalVnd);
        Assert.Equal(expectedRemaining, calc.RemainingToPayVnd);
        Assert.Equal(expectedOverpaid, calc.OverpaidVnd);
        Assert.Equal(expectedOverpaid > 0, calc.IsOverpaid);
        Assert.Equal(expectedOverpaid > 0, calc.NeedsRefund);
    }

    [Theory]
    // Branch 2: cancelled & delivery_failed prioritize refunded > 0 and set NeedsRefund = (net > 0), Remaining = 0, Overpaid = 0.
    [InlineData(OrderStatus.Cancelled, 0L, 0L, 0L, 500_000L, OrderPaymentStatus.Unpaid, false)]
    [InlineData(OrderStatus.Cancelled, 0L, 0L, 0L, 0L, OrderPaymentStatus.Unpaid, false)]
    [InlineData(OrderStatus.Cancelled, 200_000L, 0L, 0L, 500_000L, OrderPaymentStatus.PartiallyPaid, true)]
    [InlineData(OrderStatus.Cancelled, 500_000L, 0L, 0L, 500_000L, OrderPaymentStatus.Paid, true)]
    [InlineData(OrderStatus.Cancelled, 700_000L, 200_000L, 200_000L, 500_000L, OrderPaymentStatus.PartiallyRefunded, true)]
    [InlineData(OrderStatus.Cancelled, 500_000L, 500_000L, 0L, 500_000L, OrderPaymentStatus.Refunded, false)]
    [InlineData(OrderStatus.DeliveryFailed, 0L, 0L, 0L, 500_000L, OrderPaymentStatus.Unpaid, false)]
    [InlineData(OrderStatus.DeliveryFailed, 500_000L, 0L, 0L, 500_000L, OrderPaymentStatus.Paid, true)]
    [InlineData(OrderStatus.DeliveryFailed, 700_000L, 200_000L, 200_000L, 500_000L, OrderPaymentStatus.PartiallyRefunded, true)]
    [InlineData(OrderStatus.DeliveryFailed, 500_000L, 500_000L, 0L, 500_000L, OrderPaymentStatus.Refunded, false)]
    public void BrPay02CancelledAndDeliveryFailedKeepRefundedPrecedenceAndNeedsRefund(
        string orderStatus,
        long paid,
        long refunded,
        long overpaymentRefunded,
        long total,
        string expectedStatus,
        bool expectedNeedsRefund)
    {
        var calc = OrderPaymentStatusCalculator.Calculate(paid, refunded, overpaymentRefunded, total, orderStatus, isBackfilled: false);

        Assert.Equal(expectedStatus, calc.Status);
        Assert.Equal(expectedNeedsRefund, calc.NeedsRefund);
        Assert.False(calc.IsOverpaid);
        Assert.Equal(0L, calc.OverpaidVnd);
        Assert.Equal(0L, calc.RemainingToPayVnd);
    }

    [Fact]
    public void BrPay02BackfilledOrderAlwaysReturnsUnpaidAndFailsClosedIfLedgerNonZero()
    {
        var backfilledZero = OrderPaymentStatusCalculator.Calculate(0L, 0L, 0L, 0L, OrderStatus.Delivered, isBackfilled: true);
        Assert.Equal(OrderPaymentStatus.Unpaid, backfilledZero.Status);
        Assert.Equal(0L, backfilledZero.RemainingToPayVnd);
        Assert.Equal(0L, backfilledZero.OverpaidVnd);
        Assert.False(backfilledZero.IsOverpaid);
        Assert.False(backfilledZero.NeedsRefund);

        var backfilledNormal = OrderPaymentStatusCalculator.Calculate(0L, 0L, 0L, 1_500_000L, OrderStatus.Delivered, isBackfilled: true);
        Assert.Equal(OrderPaymentStatus.Unpaid, backfilledNormal.Status);
        Assert.Equal(0L, backfilledNormal.RemainingToPayVnd);
        Assert.False(backfilledNormal.NeedsRefund);

        Assert.Equal("backfilled_order_immutable", Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.Calculate(100_000L, 0L, 0L, 500_000L, OrderStatus.Delivered, isBackfilled: true)).Code);
        Assert.Equal("backfilled_order_immutable", Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.Calculate(0L, 100_000L, 0L, 500_000L, OrderStatus.Delivered, isBackfilled: true)).Code);
    }

    [Theory]
    [InlineData(-1L, 0L, 0L, 500_000L, OrderStatus.Pending, "validation_failed")]
    [InlineData(500_000L, -1L, 0L, 500_000L, OrderStatus.Pending, "validation_failed")]
    [InlineData(500_000L, 0L, -1L, 500_000L, OrderStatus.Pending, "validation_failed")]
    [InlineData(500_000L, 100_000L, 100_001L, 500_000L, OrderStatus.Pending, "validation_failed")]
    [InlineData(500_000L, 0L, 0L, -1L, OrderStatus.Pending, "validation_failed")]
    [InlineData(500_000L, 0L, 0L, 500_000L, "unknown_status", "validation_failed")]
    [InlineData(200_000L, 300_000L, 0L, 500_000L, OrderStatus.Confirmed, "negative_net_payment")]
    public void BrPay02InvalidInputsOrNegativeNetThrowDomainException(
        long paid,
        long refunded,
        long overpaymentRefunded,
        long total,
        string orderStatus,
        string expectedCode)
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.Calculate(paid, refunded, overpaymentRefunded, total, orderStatus, isBackfilled: false));
        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public void BrPay02ZeroVndWebAndAdminOrdersInitializeAsPaidWithoutAutoConfirm()
    {
        var freeLine = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "AM-FREE-0001", "Qua tang tra", null, 1, 100_000L, 100_000L, OrderPriceSource.List, null);

        var webZero = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261126Z001", CustomerId, null, OrderPaymentMethod.BankTransfer,
            Now.AddHours(24), Contact, [freeLine], subtotalVnd: 100_000L, discountVnd: 100_000L, shippingFeeVnd: 0L, totalVnd: 0L,
            null, "Voucher 100%", Attribution, null, Now);
        Assert.Equal(OrderStatus.Pending, webZero.Status);
        Assert.Equal(OrderPaymentStatus.Paid, webZero.PaymentStatus);

        var adminZero = Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261126Z002", SalesChannel.ZaloPersonal, CustomerId, Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, Contact, [freeLine], subtotalVnd: 100_000L, discountVnd: 100_000L, shippingFeeVnd: 0L, totalVnd: 0L,
            null, "Giam 100%", Attribution, null, null, Now, confirmNow: false);
        Assert.Equal(OrderStatus.Pending, adminZero.Status);
        Assert.Equal(OrderPaymentStatus.Paid, adminZero.PaymentStatus);

        // Staff can confirm and ship a 0-VND web bank_transfer order without fake payment rows.
        webZero.Confirm(OrderActorType.Staff, Guid.CreateVersion7(), Now.AddMinutes(1), codRequiresCall: false);
        webZero.MarkShipping(Guid.CreateVersion7(), Now.AddMinutes(2), hasOrderShipment: true);
        Assert.Equal(OrderStatus.Shipping, webZero.Status);
        Assert.Equal(OrderPaymentStatus.Paid, webZero.PaymentStatus);
    }

    [Fact]
    public void BrRet02CumulativeHalfUpReturnCeilingIsExactFor800kAnd700kAndHalfCases()
    {
        var line = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "AM-TN-0001", "Am Tu Sa", "180ml", quantity: 3, listPriceVnd: 300_000L, unitPriceVnd: 300_000L,
            OrderPriceSource.List, null, returnedQuantity: 3);

        // Case A (800k netMerch): subtotal 900k, discount 100k, shipping 30k -> 3 sequential returns of qty 1 give 266_667 + 266_666 + 266_667 = 800_000.
        var order800 = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261126R800", CustomerId, null, OrderPaymentMethod.BankTransfer,
            Now.AddHours(24), Contact, [line], 900_000L, 100_000L, 30_000L, 830_000L,
            null, null, Attribution, null, Now);

        var r1 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(order800, [new(line.Id, 1)]);
        Assert.Equal(266_667L, r1.CumulativeGoodsCeilingVnd);
        Assert.Equal(266_667L, r1.MaxGoodsRefundVnd);
        Assert.False(r1.IsFullRefundReturnCompleted);

        var r2 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            order800,
            [new(line.Id, 1)],
            new Dictionary<Guid, int> { [line.Id] = 1 },
            priorReturnGoodsRefundedVnd: 266_667L);
        Assert.Equal(533_333L, r2.CumulativeGoodsCeilingVnd);
        Assert.Equal(266_666L, r2.MaxGoodsRefundVnd);
        Assert.False(r2.IsFullRefundReturnCompleted);

        var r3 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            order800,
            [new(line.Id, 1)],
            new Dictionary<Guid, int> { [line.Id] = 2 },
            priorReturnGoodsRefundedVnd: 533_333L);
        Assert.Equal(800_000L, r3.CumulativeGoodsCeilingVnd);
        Assert.Equal(266_667L, r3.MaxGoodsRefundVnd);
        Assert.True(r3.IsFullRefundReturnCompleted);
        Assert.Equal(800_000L, r1.MaxGoodsRefundVnd + r2.MaxGoodsRefundVnd + r3.MaxGoodsRefundVnd);

        // Case B (700k netMerch): subtotal 900k, discount 200k -> 3 sequential returns of qty 1 give 233_333 + 233_334 + 233_333 = 700_000 (true half-up!).
        var order700 = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261126R700", CustomerId, null, OrderPaymentMethod.BankTransfer,
            Now.AddHours(24), Contact, [line], 900_000L, 200_000L, 30_000L, 730_000L,
            null, null, Attribution, null, Now);

        var b1 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(order700, [new(line.Id, 1)]);
        Assert.Equal(233_333L, b1.MaxGoodsRefundVnd);
        Assert.False(b1.IsFullRefundReturnCompleted);

        var b2 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            order700,
            [new(line.Id, 1)],
            new Dictionary<Guid, int> { [line.Id] = 1 },
            priorReturnGoodsRefundedVnd: 233_333L);
        Assert.Equal(466_667L, b2.CumulativeGoodsCeilingVnd);
        Assert.Equal(233_334L, b2.MaxGoodsRefundVnd);
        Assert.False(b2.IsFullRefundReturnCompleted);

        var b3 = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            order700,
            [new(line.Id, 1)],
            new Dictionary<Guid, int> { [line.Id] = 2 },
            priorReturnGoodsRefundedVnd: 466_667L);
        Assert.Equal(700_000L, b3.CumulativeGoodsCeilingVnd);
        Assert.Equal(233_333L, b3.MaxGoodsRefundVnd);
        Assert.True(b3.IsFullRefundReturnCompleted);
        Assert.Equal(700_000L, b1.MaxGoodsRefundVnd + b2.MaxGoodsRefundVnd + b3.MaxGoodsRefundVnd);

        // Case C: exact .5 half-up boundary (2 x 100_000 = 200_000, discount = 1 -> netMerch = 199_999 -> half is 99_999.5 -> rounds up to 100_000).
        var lineHalf = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "CH-TN-0002", "Chen Tra", null, quantity: 2, listPriceVnd: 100_000L, unitPriceVnd: 100_000L,
            OrderPriceSource.List, null);
        var orderHalf = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261126RHAF", CustomerId, null, OrderPaymentMethod.BankTransfer,
            Now.AddHours(24), Contact, [lineHalf], 200_000L, 1L, 0L, 199_999L,
            null, null, Attribution, null, Now);
        Assert.Equal(100_000L, OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(orderHalf, [new(lineHalf.Id, 1)]).MaxGoodsRefundVnd);

        // Case D: prior adjustment goods reduce remaining merchandise ceiling even on full return.
        var afterAdj = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            order800,
            [new(line.Id, 3)],
            priorAdjustmentGoodsRefundedVnd: 200_000L);
        Assert.Equal(800_000L, afterAdj.CumulativeGoodsCeilingVnd);
        Assert.Equal(600_000L, afterAdj.RemainingMerchandiseCeilingVnd);
        Assert.Equal(600_000L, afterAdj.MaxGoodsRefundVnd);
        Assert.True(afterAdj.IsFullRefundReturnCompleted);
    }

    [Fact]
    public void BrRet02OrderWithZeroVndLineRequiresEveryLineReturnedForFullCompletionAndGuardsBadInput()
    {
        var paidLine = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "AM-TN-0010", "Am Tra", null, quantity: 1, listPriceVnd: 500_000L, unitPriceVnd: 500_000L,
            OrderPriceSource.List, null);
        var zeroLine = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "QT-TN-0011", "Qua Tang 0d", null, quantity: 1, listPriceVnd: 50_000L, unitPriceVnd: 0L,
            OrderPriceSource.Override, "Qua tang kem don");

        var orderWithGift = Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261126GIFT", SalesChannel.Phone, CustomerId, Guid.CreateVersion7(),
            OrderPaymentMethod.BankTransfer, Contact, [paidLine, zeroLine], 500_000L, 0L, 30_000L, 530_000L,
            null, null, Attribution, null, null, Now);

        // Returning ONLY the paid line reaches cumGross == subtotal, yet zeroLine is unreturned -> NOT full return!
        var partialOnlyPaid = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(orderWithGift, [new(paidLine.Id, 1)]);
        Assert.Equal(500_000L, partialOnlyPaid.MaxGoodsRefundVnd);
        Assert.False(partialOnlyPaid.IsFullRefundReturnCompleted);

        // Returning zeroLine too completes 100% of every order line -> full return!
        var completeWithGift = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
            orderWithGift,
            [new(zeroLine.Id, 1)],
            new Dictionary<Guid, int> { [paidLine.Id] = 1 },
            priorReturnGoodsRefundedVnd: 500_000L);
        Assert.Equal(0L, completeWithGift.MaxGoodsRefundVnd);
        Assert.True(completeWithGift.IsFullRefundReturnCompleted);

        // Guard checks: empty lines, cumulative qty > orderLine.Quantity, unknown orderLineId.
        Assert.Equal("return_request_has_no_lines", Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(orderWithGift, [])).Code);
        Assert.Equal("return_quantity_exceeds_order", Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
                orderWithGift, [new(paidLine.Id, 1)], new Dictionary<Guid, int> { [paidLine.Id] = 1 })).Code);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(orderWithGift, [new(Guid.CreateVersion7(), 1)])).Code);
    }

    [Fact]
    public void BrPay03OrderApplyPaymentCalculationAndSystemConfirmEmitHistoryAndReasonCode()
    {
        var line = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "AM-CN-0001", "Am Chu Ne", null, 1, 500_000L, 500_000L, OrderPriceSource.List, null);
        var dueAt = Now.AddHours(24);
        var order = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261125M8P4", CustomerId, null, OrderPaymentMethod.BankTransfer,
            dueAt, Contact, [line], 500_000L, 0L, 0L, 500_000L,
            null, null, Attribution, null, Now);
        order.DrainNewHistory();
        order.DrainEvents();

        // 1. Mismatched total rejected.
        var wrongCalc = OrderPaymentStatusCalculator.Calculate(500_000L, 0L, 0L, 400_000L, order.Status, false);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            order.ApplyPaymentCalculation(wrongCalc, Now.AddMinutes(1))).Code);

        // 2. Overpayment + overpayment refund keeps 'paid', allowing Confirm and MarkShipping (O3a=C).
        var overpaidCalc = OrderPaymentStatusCalculator.Calculate(700_000L, 200_000L, 200_000L, 500_000L, order.Status, false);
        order.ApplyPaymentCalculation(overpaidCalc, Now.AddMinutes(5));
        Assert.Equal(OrderPaymentStatus.Paid, order.PaymentStatus);

        order.Confirm(
            OrderActorType.System,
            actorId: null,
            at: Now.AddMinutes(5),
            codRequiresCall: false,
            codCallConfirmed: false,
            note: OrderConfirmReasonCodes.AutoConfirmedOnFullPayment,
            reasonCode: OrderConfirmReasonCodes.AutoConfirmedOnFullPayment);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(Now.AddMinutes(5), order.ConfirmedAt);
        Assert.Equal(dueAt, order.PaymentDueAt); // R-P207-03: preserves payment_due_at for ck_order_due.

        // MarkShipping succeeds because payment_status remained 'paid' after overpayment refund!
        order.MarkShipping(Guid.CreateVersion7(), Now.AddMinutes(10), hasOrderShipment: true);
        Assert.Equal(OrderStatus.Shipping, order.Status);

        var history = order.DrainNewHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal(OrderActorType.System, history[0].ActorType);
        Assert.Null(history[0].ActorId);
        Assert.Equal(OrderConfirmReasonCodes.AutoConfirmedOnFullPayment, history[0].Reason);

        var events = order.DrainEvents();
        Assert.Equal(OrderEventTypes.OrderConfirmed, events[0].EventType);
        Assert.Equal(OrderConfirmReasonCodes.AutoConfirmedOnFullPayment, events[0].ReasonCode);

        // 3. Staff actor cannot attach system auto-confirm reasonCode.
        var order2 = Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261125M8P5", SalesChannel.Phone, CustomerId, Guid.CreateVersion7(),
            OrderPaymentMethod.BankTransfer, Contact, [line], 500_000L, 0L, 0L, 500_000L,
            null, null, Attribution, null, null, Now);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            order2.Confirm(OrderActorType.Staff, Guid.CreateVersion7(), Now.AddMinutes(1), false, false, null,
                OrderConfirmReasonCodes.AutoConfirmedOnFullPayment)).Code);
    }
}
