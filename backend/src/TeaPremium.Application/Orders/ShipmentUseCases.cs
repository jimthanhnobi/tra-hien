using FluentValidation;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record CreateOrderShipment(
    Guid OrderId,
    string CarrierName,
    string? TrackingCode,
    long? ShippingCostVnd,
    string? Note,
    Guid ActorStaffId) : ICommand<ShipmentDetailView>;

public sealed record PatchShipment(
    Guid ShipmentId,
    PatchShipmentChanges Changes,
    Guid ActorStaffId) : ICommand<ShipmentDetailView>;

public sealed record RecordShipmentEvent(
    Guid ShipmentId,
    string Status,
    DateTimeOffset OccurredAt,
    string? Note,
    long? ReturnCostVnd,
    Guid ActorStaffId) : ICommand<ShipmentDetailView>;

public sealed class CreateOrderShipmentValidator : AbstractValidator<CreateOrderShipment>
{
    public CreateOrderShipmentValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.CarrierName)
            .Must(v => !string.IsNullOrWhiteSpace(v)
                && v.Trim().Length is >= 1 and <= Shipment.MaxCarrierNameLength
                && !v.Any(char.IsControl))
            .WithMessage("Tên đơn vị vận chuyển là bắt buộc (1..100 ký tự) và không chứa ký tự điều khiển.");
        RuleFor(x => x.TrackingCode)
            .Must(v => v is null
                || (v.Trim().Length <= Shipment.MaxTrackingCodeLength && !v.Any(char.IsControl)))
            .WithMessage("Mã vận đơn không hợp lệ.");
        RuleFor(x => x.ShippingCostVnd)
            .Must(v => v is null || v.Value >= 0)
            .WithMessage("Chi phí vận chuyển không được âm.");
        RuleFor(x => x.Note)
            .Must(v => v is null
                || (v.Trim().Length <= Shipment.MaxNoteLength && !HasDisallowedNoteControl(v)))
            .WithMessage("Ghi chú vận đơn không hợp lệ.");
    }

    internal static bool HasDisallowedNoteControl(string value) =>
        value.Any(c => char.IsControl(c) && c is not ('\n' or '\r'));
}

public sealed class PatchShipmentValidator : AbstractValidator<PatchShipment>
{
    public PatchShipmentValidator()
    {
        RuleFor(x => x.ShipmentId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.Changes).NotNull();
        RuleFor(x => x.Changes)
            .Must(c => c is not null && (c.HasTrackingCode || c.HasShippingCostVnd || c.HasReturnCostVnd))
            .WithMessage("Cần cung cấp ít nhất một trường hợp lệ để cập nhật vận đơn.");
        RuleFor(x => x.Changes.TrackingCode)
            .Must(v => v is null
                || (!string.IsNullOrWhiteSpace(v)
                    && v.Trim().Length is >= 1 and <= Shipment.MaxTrackingCodeLength
                    && !v.Any(char.IsControl)))
            .When(x => x.Changes is { HasTrackingCode: true })
            .WithMessage("Mã vận đơn không hợp lệ (gửi null để xóa mã vận đơn).");
        RuleFor(x => x.Changes.ShippingCostVnd)
            .Must(v => v is null || v.Value >= 0)
            .When(x => x.Changes is { HasShippingCostVnd: true })
            .WithMessage("Chi phí vận chuyển không được âm.");
        RuleFor(x => x.Changes.ReturnCostVnd)
            .Must(v => v is null || v.Value >= 0)
            .When(x => x.Changes is { HasReturnCostVnd: true })
            .WithMessage("Chi phí hoàn hàng không được âm.");
    }
}

public sealed class RecordShipmentEventValidator : AbstractValidator<RecordShipmentEvent>
{
    public RecordShipmentEventValidator()
    {
        RuleFor(x => x.ShipmentId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.Status)
            .Must(ShipmentStatus.IsEventRecordable)
            .WithMessage("Trạng thái sự kiện phải là picked_up, in_transit, delivered, failed hoặc returned.");
        RuleFor(x => x.OccurredAt).NotEqual(default(DateTimeOffset));
        RuleFor(x => x.Note)
            .Must(v => v is null
                || (v.Trim().Length <= Shipment.MaxNoteLength
                    && !CreateOrderShipmentValidator.HasDisallowedNoteControl(v)))
            .WithMessage("Ghi chú sự kiện vận đơn không hợp lệ.");
        // GAP-P208-02 = A (Owner-approved 2026-11-26): returnCostVnd is allowed ONLY when status == 'returned' and >= 0.
        RuleFor(x => x.ReturnCostVnd)
            .Must(v => v is null || v.Value >= 0)
            .WithMessage("Chi phí hoàn hàng không được âm.");
        RuleFor(x => x)
            .Must(x => !x.ReturnCostVnd.HasValue || x.Status == ShipmentStatus.Returned)
            .WithMessage("Chi phí hoàn hàng chỉ được truyền cùng sự kiện returned.");
    }
}

public sealed class ShipmentHandlers(
    IOrderRepository orders,
    IShipmentRepository shipments,
    IOrderPaymentLedgerReader paymentLedger,
    IOrderFulfillmentTransitions fulfillment,
    IInventoryReservationService reservations,
    IEnumerable<IShippingProvider> shippingProviders,
    IEnumerable<IReplacementShipmentHook> replacementHooks,
    IClock clock) :
    ICommandHandler<CreateOrderShipment, ShipmentDetailView>,
    ICommandHandler<PatchShipment, ShipmentDetailView>,
    ICommandHandler<RecordShipmentEvent, ShipmentDetailView>
{
    public async Task<Result<ShipmentDetailView>> Handle(CreateOrderShipment command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;

        // L1: lock sales.orders first (strict lock order L1 -> L1b -> L2 -> L3 -> L4).
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, expectedVersion: null, ct);
        if (loaded.Order is not { } order)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        if (order.Status == OrderStatus.Shipping && await orders.HasActiveOrderShipmentAsync(order.Id, ct))
            return Result.Failure<ShipmentDetailView>(MapConflictError("active_shipment_exists"));

        if (order.Status != OrderStatus.Confirmed)
            return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Chỉ đơn hàng ở trạng thái đã xác nhận mới có thể tạo vận đơn.", ErrorType.Conflict));

        // BR-SHP-02 / O3a = C: reuse P2-07's IOrderPaymentLedgerReader + OrderPaymentStatusCalculator under L1 lock.
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var paymentCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        if (!order.IsBackfilled)
            order.ApplyPaymentCalculation(paymentCalc, now);

        if (order.PaymentMethod != OrderPaymentMethod.Cod && order.PaymentStatus != OrderPaymentStatus.Paid)
            return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Đơn chuyển khoản phải thanh toán đủ trước khi xuất kho giao hàng.", ErrorType.Conflict));

        var codAmountVnd = Shipment.ComputeOrderCodAmountVnd(order.PaymentMethod, paymentCalc);
        var provider = shippingProviders.Single(p => string.Equals(p.Code, ShippingProviderCode.Manual, StringComparison.Ordinal));
        var providerResult = await provider.CreateAsync(new CreateProviderShipmentRequest(
            order.Id,
            order.Code,
            ShipmentPurpose.Order,
            command.CarrierName,
            command.TrackingCode,
            codAmountVnd,
            command.ShippingCostVnd,
            command.Note), ct);

        var shipment = Shipment.CreateForOrder(
            Guid.CreateVersion7(),
            order.Id,
            providerResult.ProviderCode,
            providerResult.CarrierName,
            providerResult.TrackingCode,
            codAmountVnd,
            providerResult.ShippingCostVnd,
            command.Note,
            command.ActorStaffId,
            now);

        // SAVEPOINT-protected insert catches 23505 on ux_shipment_active_order and ux_shipment_tracking cleanly.
        var inserted = await shipments.InsertAsync(shipment, ct);
        if (!inserted.IsSuccess)
            return Result.Failure<ShipmentDetailView>(MapConflictError(inserted.ConflictCode!));

        // Transition confirmed -> shipping, consume reservations, lock unit_cost_vnd, sync BR-SEO-21 & enqueue OrderShipped in SAME tx.
        var shipped = await fulfillment.TransitionLoadedOrderToShippingAsync(order, command.ActorStaffId, command.Note, now, ct);
        if (!shipped.IsSuccess)
            return Result.Failure<ShipmentDetailView>(shipped.Error);

        return Result.Success(ShipmentDetailView.From(shipment, order));
    }

    public async Task<Result<ShipmentDetailView>> Handle(PatchShipment command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var lookup = await shipments.FindOrderLookupAsync(command.ShipmentId, ct);
        if (lookup is null)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy vận đơn.", ErrorType.NotFound));

        // Strict lock order: L1 sales.orders -> L1b sales.shipments.
        var loadedOrder = await orders.LoadForUpdateAsync(lookup.OrderId, expectedVersion: null, ct);
        if (loadedOrder.Order is not { } order)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        var shipment = await shipments.LoadForUpdateAsync(command.ShipmentId, ct);
        if (shipment is null || shipment.OrderId != order.Id)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy vận đơn.", ErrorType.NotFound));

        shipment.UpdateMetadata(
            command.Changes.HasTrackingCode,
            command.Changes.TrackingCode,
            command.Changes.HasShippingCostVnd,
            command.Changes.ShippingCostVnd,
            command.Changes.HasReturnCostVnd,
            command.Changes.ReturnCostVnd);

        var saved = await shipments.SaveUpdatedAsync(shipment, ct);
        if (!saved.IsSuccess)
            return Result.Failure<ShipmentDetailView>(MapConflictError(saved.ConflictCode!));

        return Result.Success(ShipmentDetailView.From(shipment, order));
    }

    public async Task<Result<ShipmentDetailView>> Handle(RecordShipmentEvent command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;

        var lookup = await shipments.FindOrderLookupAsync(command.ShipmentId, ct);
        if (lookup is null)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy vận đơn.", ErrorType.NotFound));

        // Strict lock order: L1 sales.orders -> L1b sales.shipments (before any L2..L4 inventory locks on returned).
        var loadedOrder = await orders.LoadForUpdateAsync(lookup.OrderId, expectedVersion: null, ct);
        if (loadedOrder.Order is not { } order)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        var shipment = await shipments.LoadForUpdateAsync(command.ShipmentId, ct);
        if (shipment is null || shipment.OrderId != order.Id)
            return Result.Failure<ShipmentDetailView>(new("not_found", "Không tìm thấy vận đơn.", ErrorType.NotFound));

        if (shipment.Purpose == ShipmentPurpose.Replacement)
        {
            var replacementEntry = shipment.RecordEvent(
                command.Status,
                ShipmentEventSource.Manual,
                command.OccurredAt,
                now,
                shipment.CreatedAt,
                command.Note,
                command.ReturnCostVnd,
                command.ActorStaffId);

            var savedReplacement = await shipments.SaveUpdatedAsync(shipment, ct);
            if (!savedReplacement.IsSuccess)
                return Result.Failure<ShipmentDetailView>(MapConflictError(savedReplacement.ConflictCode!));

            foreach (var hook in replacementHooks)
                await hook.OnReplacementShipmentEventAsync(shipment, replacementEntry, ct);

            return Result.Success(ShipmentDetailView.From(shipment, order));
        }

        // Layer 1 state machine & timestamp validation under L1 + L1b locks.
        var entry = shipment.RecordEvent(
            command.Status,
            ShipmentEventSource.Manual,
            command.OccurredAt,
            now,
            order.ShippedAt,
            command.Note,
            command.ReturnCostVnd,
            command.ActorStaffId);

        switch (command.Status)
        {
            case ShipmentStatus.PickedUp:
            case ShipmentStatus.InTransit:
                {
                    if (order.Status != OrderStatus.Shipping)
                        return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Đơn hàng không ở trạng thái đang giao.", ErrorType.Conflict));

                    var saved = await shipments.SaveUpdatedAsync(shipment, ct);
                    if (!saved.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(MapConflictError(saved.ConflictCode!));
                    break;
                }

            case ShipmentStatus.Delivered:
                {
                    if (order.Status != OrderStatus.Shipping)
                        return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Chỉ đơn đang giao mới có thể đánh dấu đã giao.", ErrorType.Conflict));

                    // Flush shipment status = 'delivered' BEFORE TransitionLoadedOrderToDeliveredAsync
                    // so CodAwaitingSettlementDeliveredHook sees the delivered shipment in the SAME transaction.
                    var saved = await shipments.SaveUpdatedAsync(shipment, ct);
                    if (!saved.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(MapConflictError(saved.ConflictCode!));

                    var delivered = await fulfillment.TransitionLoadedOrderToDeliveredAsync(
                        order,
                        OrderActorType.Staff,
                        command.ActorStaffId,
                        command.OccurredAt,
                        now,
                        entry.Note,
                        ct);
                    if (!delivered.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(delivered.Error);
                    break;
                }

            case ShipmentStatus.Failed:
                {
                    if (order.Status != OrderStatus.Shipping)
                        return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Chỉ đơn đang giao mới có thể đánh dấu giao thất bại.", ErrorType.Conflict));

                    var saved = await shipments.SaveUpdatedAsync(shipment, ct);
                    if (!saved.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(MapConflictError(saved.ConflictCode!));

                    // BR-SHP-03: failed moves order -> delivery_failed & recalculates payment_status (O1=A/O3a=C),
                    // WITHOUT restocking inventory (goods are still in return transit).
                    var failed = await fulfillment.TransitionLoadedOrderToDeliveryFailedAsync(
                        order,
                        OrderActorType.Staff,
                        command.ActorStaffId,
                        entry.Note!,
                        command.OccurredAt,
                        now,
                        ct);
                    if (!failed.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(failed.Error);
                    break;
                }

            case ShipmentStatus.Returned:
                {
                    if (order.Status != OrderStatus.DeliveryFailed)
                        return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Chỉ đơn giao thất bại mới được xác nhận hàng hoàn về kho.", ErrorType.Conflict));

                    // Layer 2 anti-double-restock guard: verify no prior return_restock movement exists for ref_type='order', ref_id=order.Id.
                    if (await shipments.HasOrderReturnRestockMovementAsync(order.Id, ct))
                        return Result.Failure<ShipmentDetailView>(new("invalid_status_transition", "Đơn hàng này đã được nhập hoàn tồn kho trước đó.", ErrorType.Conflict));

                    var saved = await shipments.SaveUpdatedAsync(shipment, ct);
                    if (!saved.IsSuccess)
                        return Result.Failure<ShipmentDetailView>(MapConflictError(saved.ConflictCode!));

                    // G16 / BR-SHP-03 / BR-INV-05 / BR-SEO-21: restock with ref_type='order', unit_cost_vnd=order_line.UnitCostVnd,
                    // recalculate weighted-average cost, clear products.sold_out_at and emit StorefrontInvalidate.
                    var restockLines = order.Lines
                        .Select(line => new RestockLine(line.VariantId, line.Quantity, line.UnitCostVnd))
                        .ToArray();

                    await reservations.RestockReturnedAsync(
                        order.Id,
                        "order",
                        restockLines,
                        OrderActorType.Staff,
                        command.ActorStaffId,
                        entry.Note,
                        ct);
                    break;
                }
        }

        return Result.Success(ShipmentDetailView.From(shipment, order));
    }

    private static Error MapConflictError(string code) => code switch
    {
        "active_shipment_exists" => new("active_shipment_exists", "Đơn hàng đã có vận đơn đang hoạt động.", ErrorType.Conflict),
        "tracking_code_exists" => new("tracking_code_exists", "Mã vận đơn đã tồn tại cho đơn vị vận chuyển này.", ErrorType.Conflict),
        _ => new("invalid_status_transition", "Thao tác vận đơn xung đột với trạng thái hiện tại.", ErrorType.Conflict)
    };
}

// BR-ORD-07.1 / AC-P2-07 / G22: synchronously insert COD awaiting_settlement payment inside the same
// transaction as order delivery (never for bank_transfer or 0-COD orders, never duplicated).
public sealed class CodAwaitingSettlementDeliveredHook(
    IShipmentRepository shipments,
    IClock clock) : IOrderDeliveredHook
{
    public async Task OnOrderDeliveredAsync(Order order, CancellationToken ct)
    {
        if (order.IsBackfilled || order.PaymentMethod != OrderPaymentMethod.Cod)
            return;

        var deliveredShipment = await shipments.FindDeliveredOrderShipmentAsync(order.Id, ct);
        if (deliveredShipment is null || deliveredShipment.CodAmountVnd <= 0)
            return;

        if (await shipments.HasNonVoidedCodPaymentAsync(order.Id, ct))
            return;

        var createdBy = deliveredShipment.Events
            .LastOrDefault(e => e.Status == ShipmentStatus.Delivered)?.CreatedBy
            ?? deliveredShipment.CreatedBy;

        await shipments.InsertAwaitingCodPaymentAsync(
            Guid.CreateVersion7(),
            order.Id,
            deliveredShipment.CodAmountVnd,
            createdBy,
            clock.UtcNow,
            ct);
    }
}
