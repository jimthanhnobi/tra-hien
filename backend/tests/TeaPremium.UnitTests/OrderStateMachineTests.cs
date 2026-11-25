using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.UnitTests;

public sealed class OrderStateMachineTests
{
    private static readonly Guid CustomerId = Guid.Parse("00000000-0000-0000-0000-000000001001");
    private static readonly Guid StaffId = Guid.Parse("00000000-0000-0000-0000-000000001002");
    private static readonly Guid ProductId = Guid.Parse("00000000-0000-0000-0000-000000001003");
    private static readonly Guid VariantA = Guid.Parse("00000000-0000-0000-0000-000000001004");
    private static readonly Guid VariantB = Guid.Parse("00000000-0000-0000-0000-000000001005");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static OrderContactSnapshot SampleContact() => new(
        "Synthetic Buyer", "+84900000001", "buyer@example.invalid",
        "Synthetic Receiver", "+84900000002", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "10 Duong Thu Nghiem");

    private static OrderAttributionSnapshot SampleAttribution() => new(OrderAcquisitionSource.Direct, true);

    private static OrderLineItem SampleLine(Guid? variantId = null, int qty = 1, long unitPrice = 1_000_000L, string source = OrderPriceSource.List, string? overrideReason = null) =>
        OrderLineItem.Create(Guid.CreateVersion7(), variantId ?? VariantA, ProductId, "AM-TN-0001", "Am Tu Sa", null, qty, 1_000_000L, unitPrice, source, overrideReason);

    private static Order NewPendingWebCodOrder() => Order.PlaceWeb(
        Guid.CreateVersion7(), "TP261123AB23", CustomerId, null, OrderPaymentMethod.Cod, null,
        SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, Now);

    private static Order NewPendingWebBankOrder(string paymentStatus = OrderPaymentStatus.Unpaid)
    {
        var placed = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261123AB24", CustomerId, null, OrderPaymentMethod.BankTransfer, Now.AddHours(24),
            SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, Now);
        if (paymentStatus == OrderPaymentStatus.Unpaid) return placed;
        return Order.Rehydrate(
            placed.Id, placed.Code, placed.Origin, placed.SalesChannel, placed.CustomerId, placed.CustomerAccountId,
            placed.Status, placed.PaymentMethod, paymentStatus, placed.PaymentDueAt, placed.Contact,
            placed.SubtotalVnd, placed.DiscountVnd, placed.ShippingFeeVnd, placed.TotalVnd, placed.DiscountNote,
            placed.VoucherId, placed.ReferralCode, placed.Attribution, placed.CustomerNote, placed.InternalNote,
            placed.StatusReason, placed.PlacedAt, placed.ConfirmedAt, placed.ShippedAt, placed.DeliveredAt,
            placed.FailedAt, placed.CancelledAt, placed.InvoiceNumber, placed.InvoiceIssuedAt, placed.IsBackfilled,
            placed.CreatedByStaffId, placed.CreatedAt, placed.UpdatedAt, placed.Version, placed.Lines);
    }

    [Fact]
    public void ValidLifecyclePendingToConfirmedToShippingToDeliveredAppendsHistoryAndEvents()
    {
        var order = NewPendingWebCodOrder();
        Assert.Equal(OrderStatus.Pending, order.Status);
        var h0 = Assert.Single(order.DrainNewHistory());
        Assert.Null(h0.FromStatus);
        Assert.Equal(OrderStatus.Pending, h0.ToStatus);
        Assert.Equal(OrderEventTypes.OrderPlaced, Assert.Single(order.DrainEvents()).EventType);

        order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(5), codRequiresCall: true, codCallConfirmed: true, note: "Da goi xac nhan");
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(Now.AddMinutes(5), order.ConfirmedAt);
        var h1 = Assert.Single(order.DrainNewHistory());
        Assert.Equal(OrderStatus.Pending, h1.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, h1.ToStatus);
        Assert.Equal(OrderEventTypes.OrderConfirmed, Assert.Single(order.DrainEvents()).EventType);

        order.MarkShipping(StaffId, Now.AddHours(1), hasOrderShipment: true, note: "GHN");
        Assert.Equal(OrderStatus.Shipping, order.Status);
        Assert.Equal(Now.AddHours(1), order.ShippedAt);
        Assert.Equal(OrderStatus.Shipping, Assert.Single(order.DrainNewHistory()).ToStatus);
        Assert.Equal(OrderEventTypes.OrderShipped, Assert.Single(order.DrainEvents()).EventType);

        order.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddHours(10), Now.AddHours(11), "Khach da nhan");
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(Now.AddHours(10), order.DeliveredAt);
        Assert.Equal(OrderStatus.Delivered, Assert.Single(order.DrainNewHistory()).ToStatus);
        Assert.Equal(OrderEventTypes.OrderDelivered, Assert.Single(order.DrainEvents()).EventType);
    }

    [Fact]
    public void ValidLifecycleShippingToDeliveryFailedSetsFailedAtAndReasonWithoutLeakingReasonToOutbox()
    {
        var order = NewPendingWebCodOrder();
        order.DrainNewHistory();
        order.DrainEvents();
        order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1), codRequiresCall: false);
        order.MarkShipping(StaffId, Now.AddMinutes(2), hasOrderShipment: true);
        order.DrainNewHistory();
        order.DrainEvents();

        order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "Khach khong nghe may 3 lan", Now.AddHours(24));
        Assert.Equal(OrderStatus.DeliveryFailed, order.Status);
        Assert.Equal(Now.AddHours(24), order.FailedAt);
        Assert.Equal("Khach khong nghe may 3 lan", order.StatusReason);
        var evt = Assert.Single(order.DrainEvents());
        Assert.Equal(OrderEventTypes.OrderDeliveryFailed, evt.EventType);
        Assert.Null(evt.ReasonCode);
    }

    [Fact]
    public void CancelFromPendingAllowsCustomerWithoutReasonAndSystemWithPaymentExpiredReasonCode()
    {
        var byCustomer = NewPendingWebCodOrder();
        byCustomer.DrainEvents();
        byCustomer.Cancel(OrderActorType.Customer, null, null, Now.AddMinutes(10));
        Assert.Equal(OrderStatus.Cancelled, byCustomer.Status);
        Assert.NotNull(byCustomer.CancelledAt);
        Assert.Null(Assert.Single(byCustomer.DrainEvents()).ReasonCode);

        var bySystem = NewPendingWebBankOrder();
        bySystem.DrainEvents();
        bySystem.Cancel(OrderActorType.System, null, "payment_expired", Now.AddHours(25));
        Assert.Equal(OrderStatus.Cancelled, bySystem.Status);
        Assert.Equal("payment_expired", Assert.Single(bySystem.DrainEvents()).ReasonCode);
    }

    [Fact]
    public void CancelFromConfirmedAllowsStaffWithoutActiveShipmentAndBlocksCustomerOrActiveShipment()
    {
        var order = NewPendingWebCodOrder();
        order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1), codRequiresCall: false);

        var exCustomer = Assert.Throws<DomainException>(() => order.Cancel(OrderActorType.Customer, null, "doi y", Now.AddMinutes(2)));
        Assert.Equal("invalid_status_transition", exCustomer.Code);

        var exShipment = Assert.Throws<DomainException>(() => order.Cancel(OrderActorType.Staff, StaffId, "huy", Now.AddMinutes(2), hasActiveOrderShipment: true));
        Assert.Equal("invalid_status_transition", exShipment.Code);

        var exMissingReason = Assert.Throws<DomainException>(() => order.Cancel(OrderActorType.Staff, StaffId, "  ", Now.AddMinutes(2), hasActiveOrderShipment: false));
        Assert.Equal("cancel_reason_required", exMissingReason.Code);

        order.Cancel(OrderActorType.Staff, StaffId, "Khach doi mau", Now.AddMinutes(3), hasActiveOrderShipment: false);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.DeliveryFailed)]
    [InlineData(OrderStatus.Cancelled)]
    public void TerminalStatesRejectAllFurtherTransitions(string terminalStatus)
    {
        var order = NewPendingWebCodOrder();
        if (terminalStatus == OrderStatus.Cancelled)
        {
            order.Cancel(OrderActorType.Staff, StaffId, "huy", Now.AddMinutes(1));
        }
        else
        {
            order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1), codRequiresCall: false);
            order.MarkShipping(StaffId, Now.AddMinutes(2), hasOrderShipment: true);
            if (terminalStatus == OrderStatus.Delivered)
                order.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddMinutes(3), Now.AddMinutes(4));
            else
                order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "that bai", Now.AddMinutes(3));
        }

        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.Confirm(OrderActorType.Staff, StaffId, Now.AddHours(1), codRequiresCall: false)).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.Cancel(OrderActorType.Staff, StaffId, "x", Now.AddHours(1))).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.MarkShipping(StaffId, Now.AddHours(1), hasOrderShipment: true)).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddHours(1), Now.AddHours(2))).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "x", Now.AddHours(1))).Code);
    }

    [Fact]
    public void SkippedStateTransitionsReturnInvalidStatusTransition()
    {
        var pending = NewPendingWebCodOrder();
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.MarkShipping(StaffId, Now.AddMinutes(1), hasOrderShipment: true)).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddMinutes(1), Now.AddMinutes(2))).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "x", Now.AddMinutes(1))).Code);

        pending.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1), codRequiresCall: false);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(2), codRequiresCall: false)).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddMinutes(2), Now.AddMinutes(3))).Code);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => pending.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "x", Now.AddMinutes(2))).Code);
    }

    [Fact]
    public void AdminOrderRejectsWebSalesChannelAndSupportsConfirmNow()
    {
        // R-P201-04 / BR-ORD-04
        var exWebChannel = Assert.Throws<DomainException>(() => Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261123AB25", SalesChannel.Web, CustomerId, StaffId, OrderPaymentMethod.Cod,
            SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, null, Now));
        Assert.Equal("invalid_sales_channel", exWebChannel.Code);

        var exCodNoCall = Assert.Throws<DomainException>(() => Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261123AB26", SalesChannel.Phone, CustomerId, StaffId, OrderPaymentMethod.Cod,
            SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, null, Now,
            confirmNow: true, codRequiresCall: true, codCallConfirmed: false, confirmNote: null));
        Assert.Equal("cod_confirmation_required", exCodNoCall.Code);

        var confirmedAdmin = Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261123AB27", SalesChannel.ZaloPersonal, CustomerId, StaffId, OrderPaymentMethod.Cod,
            SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, "Noi bo", Now,
            confirmNow: true, codRequiresCall: true, codCallConfirmed: true, confirmNote: "Chot qua Zalo");
        Assert.Equal(OrderStatus.Confirmed, confirmedAdmin.Status);
        Assert.Null(confirmedAdmin.PaymentDueAt);
        Assert.Equal(Now, confirmedAdmin.ConfirmedAt);
        Assert.Equal([OrderEventTypes.OrderPlaced, OrderEventTypes.OrderConfirmed], confirmedAdmin.DrainEvents().Select(x => x.EventType).ToArray());
    }

    [Fact]
    public void WebBankTransferRequiresPaymentDueAtAndPaidStatusBeforeConfirmOrShip()
    {
        var exNoDue = Assert.Throws<DomainException>(() => Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261123AB28", CustomerId, null, OrderPaymentMethod.BankTransfer, null,
            SampleContact(), [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, null, SampleAttribution(), null, Now));
        Assert.Equal("validation_failed", exNoDue.Code);

        var unpaid = NewPendingWebBankOrder(OrderPaymentStatus.Unpaid);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => unpaid.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1))).Code);

        var paid = NewPendingWebBankOrder(OrderPaymentStatus.Paid);
        paid.Confirm(OrderActorType.System, null, Now.AddMinutes(1));
        Assert.Equal(OrderStatus.Confirmed, paid.Status);

        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => paid.MarkShipping(StaffId, Now.AddMinutes(2), hasOrderShipment: false)).Code);
        paid.MarkShipping(StaffId, Now.AddMinutes(2), hasOrderShipment: true);
        Assert.Equal(OrderStatus.Shipping, paid.Status);
    }

    [Fact]
    public void MarkDeliveredValidatesDeliveredAtBounds()
    {
        var order = NewPendingWebCodOrder();
        order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(1), codRequiresCall: false);
        order.MarkShipping(StaffId, Now.AddHours(2), hasOrderShipment: true);

        Assert.Equal("invalid_delivered_at", Assert.Throws<DomainException>(() => order.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddHours(1), Now.AddHours(3))).Code);
        Assert.Equal("invalid_delivered_at", Assert.Throws<DomainException>(() => order.MarkDelivered(OrderActorType.Staff, StaffId, Now.AddHours(4), Now.AddHours(3))).Code);
    }

    [Fact]
    public void ReplaceLinesAllowedOnlyInPendingAndConfirmedAndTracksLineDeltas()
    {
        var order = NewPendingWebCodOrder();
        var originalLineId = order.Lines[0].Id;

        var updated = new[]
        {
            SampleLine(VariantA, qty: 2),
            SampleLine(VariantB, qty: 1, unitPrice: 500_000L)
        };
        var delta1 = order.ReplaceLines(updated, 2_500_000L, 0L, 0L, 2_500_000L, null, null, Now.AddMinutes(5));
        Assert.Single(delta1.Added);
        Assert.Empty(delta1.Removed);
        var inc = Assert.Single(delta1.QuantityIncreased);
        Assert.Equal(originalLineId, inc.After.Id);

        order.Confirm(OrderActorType.Staff, StaffId, Now.AddMinutes(6), codRequiresCall: false);
        var reducedAndRemoved = new[] { SampleLine(VariantA, qty: 1) };
        var delta2 = order.ReplaceLines(reducedAndRemoved, 1_000_000L, 0L, 0L, 1_000_000L, null, null, Now.AddMinutes(7));
        Assert.Single(delta2.Removed);
        Assert.Single(delta2.QuantityDecreased);

        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() => order.ReplaceLines([], 0L, 0L, 0L, 0L, null, null, Now.AddMinutes(8))).Code);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() => order.ReplaceLines([SampleLine(VariantA), SampleLine(VariantA)], 2_000_000L, 0L, 0L, 2_000_000L, null, null, Now.AddMinutes(8))).Code);

        order.MarkShipping(StaffId, Now.AddMinutes(9), hasOrderShipment: true);
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() => order.ReplaceLines([SampleLine(VariantA, qty: 2)], 2_000_000L, 0L, 0L, 2_000_000L, null, null, Now.AddMinutes(10))).Code);
    }

    [Fact]
    public void OrderCodeUsesVietnamDateAndUnambiguousAlphabet()
    {
        // 2026-11-23 16:59:59Z == 2026-11-23 23:59:59 +07:00
        var beforeMidnightVn = BusinessCalendar.ToVietnamDate(new DateTimeOffset(2026, 11, 23, 16, 59, 59, TimeSpan.Zero));
        // 2026-11-23 17:00:00Z == 2026-11-24 00:00:00 +07:00
        var afterMidnightVn = BusinessCalendar.ToVietnamDate(new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal("TP261123AB23", OrderCode.Format(beforeMidnightVn, "AB23"));
        Assert.Equal("TP261124AB23", OrderCode.Format(afterMidnightVn, "AB23"));
        Assert.Equal(31, OrderCode.Alphabet.Length);

        for (var i = 0; i < 200; i++)
        {
            var generated = OrderCode.GenerateRandom(afterMidnightVn);
            Assert.True(OrderCode.IsValid(generated));
            Assert.True(OrderCode.IsGeneratedFormat(generated));
            var suffix = generated[^4..];
            Assert.DoesNotContain('0', suffix);
            Assert.DoesNotContain('O', suffix);
            Assert.DoesNotContain('1', suffix);
            Assert.DoesNotContain('I', suffix);
            Assert.DoesNotContain('L', suffix);
        }
    }
}
