using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.UnitTests;

public sealed class ShipmentStateMachineTests
{
    private static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-000000008001");
    private static readonly Guid StaffId = Guid.Parse("00000000-0000-0000-0000-000000008002");
    private static readonly Guid ReturnRequestId = Guid.Parse("00000000-0000-0000-0000-000000008003");
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    private static Shipment NewOrderShipment(long codAmountVnd = 1_000_000L, string? tracking = "GHN-001", string? note = "Giao gio hanh chinh") =>
        Shipment.CreateForOrder(
            Guid.CreateVersion7(),
            OrderId,
            ShippingProviderCode.Manual,
            "GHN",
            tracking,
            codAmountVnd,
            30_000L,
            note,
            StaffId,
            CreatedAt);

    // U01
    [Theory]
    [InlineData("picked_up,in_transit,delivered")]
    [InlineData("delivered")]
    [InlineData("failed,returned")]
    [InlineData("picked_up,in_transit,in_transit,delivered")]
    public void BrShp02ValidForwardTransitionsUpdateTimestampsAndAppendEvents(string csvSequence)
    {
        var sequence = csvSequence.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var shipment = NewOrderShipment();
        Assert.Equal(ShipmentStatus.Created, shipment.Status);
        var initialEvent = Assert.Single(shipment.Events);
        Assert.Equal(ShipmentStatus.Created, initialEvent.Status);
        Assert.Equal(ShipmentEventSource.Manual, initialEvent.Source);
        Assert.Equal(CreatedAt, initialEvent.OccurredAt);
        Assert.Equal("Giao gio hanh chinh", initialEvent.Note);

        var t = CreatedAt;
        DateTimeOffset? firstPickedUp = null;
        foreach (var status in sequence)
        {
            t = t.AddHours(1);
            var note = status == ShipmentStatus.Failed ? "Khach khong nghe may 3 lan" : $"Event {status}";
            long? returnCost = status == ShipmentStatus.Returned ? 25_000L : null;
            var entry = shipment.RecordEvent(status, ShipmentEventSource.Manual, t, t.AddMinutes(5), CreatedAt, note, returnCost, StaffId);
            Assert.Equal(status, entry.Status);
            Assert.Equal(status, shipment.Status);

            if (status == ShipmentStatus.PickedUp)
            {
                firstPickedUp = t;
                Assert.Equal(t, shipment.PickedUpAt);
            }
            else if (status == ShipmentStatus.InTransit && firstPickedUp.HasValue)
            {
                Assert.Equal(firstPickedUp, shipment.PickedUpAt);
            }
            else if (status == ShipmentStatus.Delivered)
            {
                Assert.Equal(t, shipment.DeliveredAt);
            }
            else if (status == ShipmentStatus.Failed)
            {
                Assert.Equal(t, shipment.FailedAt);
            }
            else if (status == ShipmentStatus.Returned)
            {
                Assert.Equal(t, shipment.ReturnedAt);
                Assert.Equal(25_000L, shipment.ReturnCostVnd);
            }
        }

        Assert.Equal(sequence.Length + 1, shipment.Events.Count);
        Assert.Equal(sequence.Length + 1, shipment.DrainNewEvents().Count);
        Assert.Empty(shipment.PendingNewEvents);
    }

    // U02
    [Theory]
    [InlineData("delivered", ShipmentStatus.Failed)]
    [InlineData("delivered", ShipmentStatus.Returned)]
    [InlineData("delivered", ShipmentStatus.InTransit)]
    [InlineData("failed,returned", ShipmentStatus.Returned)]
    [InlineData("in_transit", ShipmentStatus.PickedUp)]
    [InlineData("", ShipmentStatus.Returned)]
    [InlineData("picked_up", ShipmentStatus.Returned)]
    [InlineData("in_transit", ShipmentStatus.Returned)]
    [InlineData("failed", ShipmentStatus.Delivered)]
    [InlineData("failed", ShipmentStatus.InTransit)]
    [InlineData("failed", ShipmentStatus.PickedUp)]
    [InlineData("failed", ShipmentStatus.Failed)]
    public void InvalidAndBackwardTransitionsThrowInvalidStatusTransition(string setupCsv, string forbiddenTarget)
    {
        var setup = setupCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var shipment = NewOrderShipment();
        var t = CreatedAt;
        foreach (var step in setup)
        {
            t = t.AddHours(1);
            shipment.RecordEvent(step, ShipmentEventSource.Manual, t, t.AddMinutes(5), CreatedAt,
                step == ShipmentStatus.Failed ? "Khach tu choi nhan" : null, null, StaffId);
        }

        var nextTime = t.AddHours(1);
        var ex = Assert.Throws<DomainException>(() => shipment.RecordEvent(
            forbiddenTarget,
            ShipmentEventSource.Manual,
            nextTime,
            nextTime.AddMinutes(5),
            CreatedAt,
            forbiddenTarget == ShipmentStatus.Failed ? "Ly do" : null,
            null,
            StaffId));
        Assert.Equal("invalid_status_transition", ex.Code);
    }

    // U03
    [Fact]
    public void CancelShipmentAllowedOnlyFromCreatedAndRejectsAfterProgress()
    {
        var created = NewOrderShipment();
        var cancelledEvent = created.Cancel(StaffId, "Huy van don nhap", CreatedAt.AddMinutes(10), CreatedAt.AddMinutes(15));
        Assert.Equal(ShipmentStatus.Cancelled, created.Status);
        Assert.Equal(ShipmentStatus.Cancelled, cancelledEvent.Status);

        // Cancelled is terminal for events.
        var afterCancelEx = Assert.Throws<DomainException>(() => created.RecordEvent(
            ShipmentStatus.PickedUp, ShipmentEventSource.Manual, CreatedAt.AddMinutes(20), CreatedAt.AddMinutes(25), CreatedAt, null, null, StaffId));
        Assert.Equal("invalid_status_transition", afterCancelEx.Code);

        foreach (var progressed in new[]
        {
            new[] { ShipmentStatus.PickedUp },
            new[] { ShipmentStatus.InTransit },
            new[] { ShipmentStatus.Delivered },
            new[] { ShipmentStatus.Failed },
            new[] { ShipmentStatus.Failed, ShipmentStatus.Returned }
        })
        {
            var s = NewOrderShipment();
            var t = CreatedAt;
            foreach (var step in progressed)
            {
                t = t.AddHours(1);
                s.RecordEvent(step, ShipmentEventSource.Manual, t, t.AddMinutes(5), CreatedAt,
                    step == ShipmentStatus.Failed ? "Khong lien lac duoc" : null, null, StaffId);
            }

            var ex = Assert.Throws<DomainException>(() => s.Cancel(StaffId, "Thu huy", t.AddHours(1), t.AddHours(2)));
            Assert.Equal("invalid_status_transition", ex.Code);
        }
    }

    // U04
    [Fact]
    public void BrShp02ComputeOrderCodAmountVndHandlesCodDepositOverpaidZeroVndAndBankTransfer()
    {
        // 1. COD unpaid: full total.
        var codUnpaid = OrderPaymentStatusCalculator.Calculate(0L, 0L, 0L, 1_500_000L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(1_500_000L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.Cod, codUnpaid));

        // 2. COD with confirmed deposit 500k: remaining = 1_000_000.
        var codDeposit = OrderPaymentStatusCalculator.Calculate(500_000L, 0L, 0L, 1_500_000L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(1_000_000L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.Cod, codDeposit));

        // 3. COD with deposit 700k + overpayment refund 200k (effective_paid = 500k): remaining = 1_000_000 (O3a=C).
        var codWithOverpayRefund = OrderPaymentStatusCalculator.Calculate(700_000L, 200_000L, 200_000L, 1_500_000L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(1_000_000L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.Cod, codWithOverpayRefund));

        // 4. COD overpaid (paid 1.7m on 1.5m): remaining = 0.
        var codOverpaid = OrderPaymentStatusCalculator.Calculate(1_700_000L, 0L, 0L, 1_500_000L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(0L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.Cod, codOverpaid));

        // 5. COD 0-VND order (O3a=C): remaining = 0.
        var codZeroVnd = OrderPaymentStatusCalculator.Calculate(0L, 0L, 0L, 0L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(0L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.Cod, codZeroVnd));

        // 6. Bank transfer (unpaid or paid): always 0 COD.
        Assert.Equal(0L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.BankTransfer, codUnpaid));
        var bankPaid = OrderPaymentStatusCalculator.Calculate(1_500_000L, 0L, 0L, 1_500_000L, OrderStatus.Confirmed, isBackfilled: false);
        Assert.Equal(0L, Shipment.ComputeOrderCodAmountVnd(OrderPaymentMethod.BankTransfer, bankPaid));
    }

    // U05
    [Fact]
    public void EventTimestampsEnforceMonotonicBoundsAndRejectFuture()
    {
        var s1 = NewOrderShipment();
        var now = CreatedAt.AddHours(5);

        // Future picked_up -> invalid_event_time.
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.PickedUp, ShipmentEventSource.Manual, now.AddMinutes(1), now, CreatedAt, null, null, StaffId)).Code);

        // Before created_at -> invalid_event_time.
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.PickedUp, ShipmentEventSource.Manual, CreatedAt.AddMinutes(-1), now, CreatedAt, null, null, StaffId)).Code);

        // Picked up at +2h, then in_transit before picked_up -> invalid_event_time.
        s1.RecordEvent(ShipmentStatus.PickedUp, ShipmentEventSource.Manual, CreatedAt.AddHours(2), now, CreatedAt, null, null, StaffId);
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.InTransit, ShipmentEventSource.Manual, CreatedAt.AddHours(1), now, CreatedAt, null, null, StaffId)).Code);

        // Delivered before shipped_at or in future -> invalid_delivered_at.
        var shippedAt = CreatedAt.AddHours(1);
        Assert.Equal("invalid_delivered_at", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.Delivered, ShipmentEventSource.Manual, shippedAt.AddMinutes(-5), now, shippedAt, null, null, StaffId)).Code);
        Assert.Equal("invalid_delivered_at", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.Delivered, ShipmentEventSource.Manual, now.AddMinutes(1), now, shippedAt, null, null, StaffId)).Code);

        // Failed at +3h, then returned before failed_at -> invalid_event_time.
        s1.RecordEvent(ShipmentStatus.Failed, ShipmentEventSource.Manual, CreatedAt.AddHours(3), now, shippedAt, "Khong co nguoi nhan", null, StaffId);
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            s1.RecordEvent(ShipmentStatus.Returned, ShipmentEventSource.Manual, CreatedAt.AddHours(2).AddMinutes(30), now, shippedAt, null, null, StaffId)).Code);
    }

    // U06
    [Fact]
    public void FailedEventRequiresNonEmptyReasonAndReturnCostAllowedOnlyOnReturned()
    {
        var shipment = NewOrderShipment();
        var now = CreatedAt.AddHours(4);

        Assert.Equal("failure_reason_required", Assert.Throws<DomainException>(() =>
            shipment.RecordEvent(ShipmentStatus.Failed, ShipmentEventSource.Manual, CreatedAt.AddHours(1), now, CreatedAt, null, null, StaffId)).Code);
        Assert.Equal("failure_reason_required", Assert.Throws<DomainException>(() =>
            shipment.RecordEvent(ShipmentStatus.Failed, ShipmentEventSource.Manual, CreatedAt.AddHours(1), now, CreatedAt, "   ", null, StaffId)).Code);

        // Passing returnCostVnd on any non-returned status -> validation_failed (GAP-P208-02 = A).
        foreach (var status in new[] { ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.Delivered, ShipmentStatus.Failed })
        {
            Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
                shipment.RecordEvent(status, ShipmentEventSource.Manual, CreatedAt.AddHours(1), now, CreatedAt,
                    status == ShipmentStatus.Failed ? "Ly do" : null, 10_000L, StaffId)).Code);
        }

        // Transition to failed, then reject negative returnCostVnd, then accept 0 and positive.
        shipment.RecordEvent(ShipmentStatus.Failed, ShipmentEventSource.Manual, CreatedAt.AddHours(1), now, CreatedAt, "Khach huy", null, StaffId);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            shipment.RecordEvent(ShipmentStatus.Returned, ShipmentEventSource.Manual, CreatedAt.AddHours(2), now, CreatedAt, "Hang ve kho", -1L, StaffId)).Code);

        shipment.RecordEvent(ShipmentStatus.Returned, ShipmentEventSource.Manual, CreatedAt.AddHours(2), now, CreatedAt, "Hang ve kho", 0L, StaffId);
        Assert.Equal(0L, shipment.ReturnCostVnd);
        Assert.Equal(ShipmentStatus.Returned, shipment.Status);
    }

    // U07
    [Fact]
    public void UpdateMetadataNormalizesTrackingCodeAndRejectsCancelledShipment()
    {
        var shipment = NewOrderShipment(tracking: "  ");
        Assert.Null(shipment.TrackingCode);

        // Trim trackingCode and set costs.
        shipment.UpdateMetadata(true, "  GHN-999  ", true, 35_000L, true, 15_000L);
        Assert.Equal("GHN-999", shipment.TrackingCode);
        Assert.Equal(35_000L, shipment.ShippingCostVnd);
        Assert.Equal(15_000L, shipment.ReturnCostVnd);

        // PATCH rejects empty/whitespace trackingCode, empty change set, and negative costs.
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            shipment.UpdateMetadata(true, "   ", false, null, false, null)).Code);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            shipment.UpdateMetadata(false, null, false, null, false, null)).Code);
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            shipment.UpdateMetadata(false, null, true, -100L, false, null)).Code);

        // Clear nullable fields explicitly with null.
        shipment.UpdateMetadata(true, null, true, null, true, null);
        Assert.Null(shipment.TrackingCode);
        Assert.Null(shipment.ShippingCostVnd);
        Assert.Null(shipment.ReturnCostVnd);

        // Allowed even on delivered / returned shipments (BR-SHP-05).
        shipment.RecordEvent(ShipmentStatus.Delivered, ShipmentEventSource.Manual, CreatedAt.AddHours(2), CreatedAt.AddHours(3), CreatedAt, null, null, StaffId);
        shipment.UpdateMetadata(false, null, true, 42_000L, false, null);
        Assert.Equal(42_000L, shipment.ShippingCostVnd);

        // Rejected on cancelled shipments.
        var cancelled = NewOrderShipment();
        cancelled.Cancel(StaffId, "Huy", CreatedAt.AddMinutes(5), CreatedAt.AddMinutes(10));
        Assert.Equal("invalid_status_transition", Assert.Throws<DomainException>(() =>
            cancelled.UpdateMetadata(false, null, true, 20_000L, false, null)).Code);
    }

    // U08
    [Fact]
    public void CreateForReplacementRequiresReturnRequestIdAndZeroCodSeamG30()
    {
        Assert.Equal("validation_failed", Assert.Throws<DomainException>(() =>
            Shipment.CreateForReplacement(Guid.CreateVersion7(), OrderId, Guid.Empty, ShippingProviderCode.Manual, "GHTK", null, 30_000L, null, StaffId, CreatedAt)).Code);

        var replacement = Shipment.CreateForReplacement(
            Guid.CreateVersion7(),
            OrderId,
            ReturnRequestId,
            ShippingProviderCode.Manual,
            "GHTK",
            "GHTK-REP-01",
            32_000L,
            "Gui am thay the",
            StaffId,
            CreatedAt);

        Assert.Equal(ShipmentPurpose.Replacement, replacement.Purpose);
        Assert.Equal(ReturnRequestId, replacement.ReturnRequestId);
        Assert.Equal(0L, replacement.CodAmountVnd);
        Assert.Equal("GHTK-REP-01", replacement.TrackingCode);
        Assert.Single(replacement.Events);
    }

    // U09
    [Fact]
    public void OrderMarkDeliveryFailedEnforcesTimestampBoundsWhenNowProvided()
    {
        var contact = new OrderContactSnapshot(
            "Buyer", "+84900000801", null,
            "Receiver", "+84900000802", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "10 Duong A");
        var line = OrderLineItem.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "AM-SHP-01", "Am Tra", null, 1, 500_000L, 500_000L, OrderPriceSource.List, null);
        var order = Order.PlaceWeb(
            Guid.CreateVersion7(), "TP261126AB23", Guid.CreateVersion7(), null, OrderPaymentMethod.Cod, null,
            contact, [line], 500_000L, 0L, 0L, 500_000L, null, null, new(OrderAcquisitionSource.Direct, true), null, CreatedAt);
        order.Confirm(OrderActorType.Staff, StaffId, CreatedAt.AddMinutes(5), codRequiresCall: false);
        var shippedAt = CreatedAt.AddHours(1);
        order.MarkShipping(StaffId, shippedAt, hasOrderShipment: true);

        var now = CreatedAt.AddHours(3);
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "Khong nhan", shippedAt.AddMinutes(-1), now)).Code);
        Assert.Equal("invalid_event_time", Assert.Throws<DomainException>(() =>
            order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "Khong nhan", now.AddMinutes(1), now)).Code);

        order.MarkDeliveryFailed(OrderActorType.Staff, StaffId, "Khach huy", CreatedAt.AddHours(2), now);
        Assert.Equal(OrderStatus.DeliveryFailed, order.Status);
        Assert.Equal(CreatedAt.AddHours(2), order.FailedAt);
        Assert.Equal(now, order.UpdatedAt);
    }
}
