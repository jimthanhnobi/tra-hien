using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Payments;

public sealed record RecordOrderPayment(
    Guid OrderId,
    string Method,
    long AmountVnd,
    DateTimeOffset ReceivedAt,
    string? BankReference,
    string? Note,
    Guid ActorStaffId) : ICommand<PaymentRecordedView>;

public sealed record VoidPayment(
    Guid PaymentId,
    string Reason,
    Guid ActorStaffId) : ICommand<PaymentVoidedView>;

public sealed record CreateOrderRefund(
    Guid OrderId,
    string Kind,
    long AmountVnd,
    long ShippingAmountVnd,
    string Method,
    string? BankReference,
    string Reason,
    Guid? ReturnRequestId,
    DateTimeOffset RefundedAt,
    Guid ActorStaffId) : ICommand<RefundRecordedView>;

public sealed record GetAwaitingPayments(
    string Type,
    string? Carrier,
    int Page,
    int PageSize) : IQuery<AwaitingPaymentsPage>;

public sealed class RecordOrderPaymentValidator : AbstractValidator<RecordOrderPayment>
{
    public RecordOrderPaymentValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.Method).Must(PaymentMethod.IsManualRecordable)
            .WithMessage("Phương thức ghi nhận thanh toán chỉ chấp nhận chuyển khoản hoặc tiền mặt.");
        RuleFor(x => x.AmountVnd).GreaterThan(0);
        RuleFor(x => x.ReceivedAt).NotEqual(default(DateTimeOffset));
        RuleFor(x => x.BankReference)
            .Must(v => v is null || (v.Trim().Length <= 128 && !v.Any(char.IsControl)))
            .WithMessage("Mã giao dịch ngân hàng không hợp lệ.");
        RuleFor(x => x.Note)
            .Must(v => v is null || (v.Trim().Length <= 1000 && !HasDisallowedControl(v)))
            .WithMessage("Ghi chú thanh toán không hợp lệ.");
    }

    internal static bool HasDisallowedControl(string value) =>
        value.Any(c => char.IsControl(c) && c is not ('\n' or '\r'));
}

public sealed class VoidPaymentValidator : AbstractValidator<VoidPayment>
{
    public VoidPaymentValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)
                && v.Trim().Length is >= 1 and <= 1000
                && !v.Any(char.IsControl))
            .WithMessage("Lý do hủy khoản thu là bắt buộc và không được chứa ký tự điều khiển.");
    }
}

public sealed class CreateOrderRefundValidator : AbstractValidator<CreateOrderRefund>
{
    public CreateOrderRefundValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorStaffId).NotEmpty();
        RuleFor(x => x.Kind).Must(RefundKind.IsValid)
            .WithMessage("Loại hoàn tiền phải là overpayment, return, cancellation hoặc adjustment.");
        RuleFor(x => x.AmountVnd).GreaterThan(0);
        RuleFor(x => x.ShippingAmountVnd)
            .GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(x => x.AmountVnd)
            .WithMessage("Phí vận chuyển hoàn phải nằm trong khoảng từ 0 đến tổng số tiền hoàn.");
        RuleFor(x => x.Method).Must(RefundMethod.IsValid)
            .WithMessage("Phương thức hoàn tiền không hợp lệ.");
        RuleFor(x => x.BankReference)
            .Must(v => v is null || (v.Trim().Length <= 128 && !v.Any(char.IsControl)))
            .WithMessage("Mã giao dịch ngân hàng hoàn tiền không hợp lệ.");
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)
                && v.Trim().Length is >= 1 and <= 1000
                && !RecordOrderPaymentValidator.HasDisallowedControl(v))
            .WithMessage("Lý do hoàn tiền là bắt buộc.");
        RuleFor(x => x.ReturnRequestId)
            .Must(id => id is null || id.Value != Guid.Empty)
            .WithMessage("Mã yêu cầu đổi trả không hợp lệ.");
        RuleFor(x => x.RefundedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class GetAwaitingPaymentsValidator : AbstractValidator<GetAwaitingPayments>
{
    public GetAwaitingPaymentsValidator()
    {
        RuleFor(x => x.Type).Must(AwaitingPaymentType.IsValid)
            .WithMessage("Loại danh sách chờ đối soát phải là bank_transfer hoặc cod.");
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Carrier)
            .Must(v => v is null || (v.Trim().Length <= 100 && !v.Any(char.IsControl)))
            .WithMessage("Tên đơn vị vận chuyển không hợp lệ.");
        RuleFor(x => x)
            .Must(x => x.Type != AwaitingPaymentType.BankTransfer || string.IsNullOrWhiteSpace(x.Carrier))
            .WithMessage("Bộ lọc đơn vị vận chuyển chỉ áp dụng khi type=cod.");
    }
}

public sealed class PaymentHandlers(
    IOrderRepository orders,
    IOrderPricingRead pricingRead,
    IOrderPaymentLedgerReader paymentLedger,
    IPaymentRepository payments,
    IRefundRepository refunds,
    IReturnRefundPort returnRefunds,
    IPaymentQueries queries,
    IOutboxWriter outbox,
    IClock clock,
    IEnumerable<IRefundRecordedHook> refundHooks) :
    ICommandHandler<RecordOrderPayment, PaymentRecordedView>,
    ICommandHandler<VoidPayment, PaymentVoidedView>,
    ICommandHandler<CreateOrderRefund, RefundRecordedView>,
    IQueryHandler<GetAwaitingPayments, AwaitingPaymentsPage>
{
    public async Task<Result<PaymentRecordedView>> Handle(RecordOrderPayment command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        if (command.ReceivedAt > now)
            return Result.Failure<PaymentRecordedView>(Error.BusinessRule("received_at_in_future", "Thời điểm nhận thanh toán không được ở tương lai."));

        var bankRef = NormalizeOptional(command.BankReference);
        var note = NormalizeOptional(command.Note);

        // L1: always lock sales.orders first.
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, expectedVersion: null, ct);
        if (loaded.Order is not { } order)
            return Result.Failure<PaymentRecordedView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        if (order.IsBackfilled)
            return Result.Failure<PaymentRecordedView>(Error.BusinessRule("backfilled_order_immutable", "Không thể ghi nhận thanh toán cho đơn lịch sử backfill."));

        if (bankRef is not null && await payments.ExistsActiveBankReferenceAsync(PaymentProvider.Manual, bankRef, ct))
            return Result.Failure<PaymentRecordedView>(new("duplicate_bank_reference", "Mã giao dịch ngân hàng đã tồn tại trên một khoản thu đang hiệu lực.", ErrorType.Conflict));

        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var priorCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        var wasAlreadyPaid = priorCalc.Status == OrderPaymentStatus.Paid;
        var paymentId = Guid.CreateVersion7();
        var record = new NewPaymentRecord(
            paymentId,
            order.Id,
            command.Method,
            PaymentProvider.Manual,
            command.AmountVnd,
            PaymentRecordStatus.Confirmed,
            bankRef,
            command.ReceivedAt,
            note,
            command.ActorStaffId,
            now,
            command.ActorStaffId,
            now);

        if (!await payments.TryInsertWithSavepointAsync(record, ct))
            return Result.Failure<PaymentRecordedView>(new("duplicate_bank_reference", "Mã giao dịch ngân hàng đã tồn tại trên một khoản thu đang hiệu lực.", ErrorType.Conflict));

        var newConfirmedPaid = checked(ledger.ConfirmedPaidVnd + command.AmountVnd);
        var calc = OrderPaymentStatusCalculator.Calculate(
            newConfirmedPaid,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(calc, now);

        // BR-PAY-03 & ASSUMPTION-P207-04=A: auto-confirm web bank_transfer pending order once recording payment reaches paid.
        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        var autoConfirmed = false;
        if (!wasAlreadyPaid
            && order.Origin == OrderOrigin.Web
            && order.Status == OrderStatus.Pending
            && order.PaymentMethod == OrderPaymentMethod.BankTransfer
            && calc.Status == OrderPaymentStatus.Paid
            && settings.AutoConfirmOnFullPayment)
        {
            order.Confirm(
                OrderActorType.System,
                actorId: null,
                at: now,
                codRequiresCall: false,
                codCallConfirmed: false,
                note: OrderConfirmReasonCodes.AutoConfirmedOnFullPayment,
                reasonCode: OrderConfirmReasonCodes.AutoConfirmedOnFullPayment);
            autoConfirmed = true;
        }

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);

        var hasApprovedRefundReturn = await returnRefunds.HasOtherApprovedRefundReturnAsync(order.Id, null, ct);

        return Result.Success(new PaymentRecordedView(
            paymentId,
            order.Id,
            order.Code,
            command.Method,
            PaymentProvider.Manual,
            PaymentRecordStatus.Confirmed,
            command.AmountVnd,
            command.ReceivedAt,
            now,
            order.Status,
            order.PaymentStatus,
            calc.ConfirmedPaidVnd,
            calc.RefundedVnd,
            calc.NetPaidVnd,
            calc.IsOverpaid,
            calc.NeedsRefund || hasApprovedRefundReturn,
            autoConfirmed,
            CatalogVersions.Encode(order.Version)));
    }

    public async Task<Result<PaymentVoidedView>> Handle(VoidPayment command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var trimmedReason = command.Reason.Trim();

        // Step 0 (R-P207-01): unlocked read to discover order_id before locking sales.orders first.
        var lookup = await payments.FindOrderForVoidAsync(command.PaymentId, ct);
        if (lookup is null)
            return Result.Failure<PaymentVoidedView>(new("not_found", "Không tìm thấy khoản thanh toán.", ErrorType.NotFound));

        // L1: lock sales.orders BEFORE L2 sales.payments to match RecordOrderPayment / CreateOrderRefund lock order.
        var loaded = await orders.LoadForUpdateAsync(lookup.OrderId, expectedVersion: null, ct);
        if (loaded.Order is not { } order)
            return Result.Failure<PaymentVoidedView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        if (order.IsBackfilled)
            return Result.Failure<PaymentVoidedView>(Error.BusinessRule("backfilled_order_immutable", "Không thể hủy khoản thanh toán của đơn lịch sử backfill."));

        // L2: lock sales.payments row.
        var payment = await payments.LoadForUpdateAsync(command.PaymentId, ct);
        if (payment is null)
            return Result.Failure<PaymentVoidedView>(new("not_found", "Không tìm thấy khoản thanh toán.", ErrorType.NotFound));

        if (payment.Status == PaymentRecordStatus.Voided)
            return Result.Failure<PaymentVoidedView>(new("payment_already_voided", "Khoản thanh toán này đã được hủy trước đó.", ErrorType.Conflict));

        if (payment.CodSettlementId is not null)
            return Result.Failure<PaymentVoidedView>(new("payment_settled_cod", "Không thể hủy khoản thanh toán đã thuộc phiên đối soát COD.", ErrorType.Conflict));

        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var confirmedAfterVoid = payment.Status == PaymentRecordStatus.Confirmed
            ? checked(ledger.ConfirmedPaidVnd - payment.AmountVnd)
            : ledger.ConfirmedPaidVnd;

        // ASSUMPTION-P207-01=A: preserve net >= 0 invariant when order already has refunds.
        if (confirmedAfterVoid < ledger.RefundedVnd)
            return Result.Failure<PaymentVoidedView>(new("payment_has_refunds", "Không thể hủy khoản thu vì đơn hàng đã ghi nhận hoàn tiền vượt số còn lại.", ErrorType.Conflict));

        // R-P201-01: sets status='voided', confirmed_at=NULL, void_reason=reason simultaneously.
        await payments.VoidAsync(payment.Id, trimmedReason, ct);

        var calc = OrderPaymentStatusCalculator.Calculate(
            confirmedAfterVoid,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(calc, now);
        await orders.SaveUpdatedOrderAsync(order, null, ct);

        var hasApprovedRefundReturn = await returnRefunds.HasOtherApprovedRefundReturnAsync(order.Id, null, ct);

        return Result.Success(new PaymentVoidedView(
            payment.Id,
            order.Id,
            order.Code,
            PaymentRecordStatus.Voided,
            payment.AmountVnd,
            order.Status,
            order.PaymentStatus,
            calc.ConfirmedPaidVnd,
            calc.RefundedVnd,
            calc.NetPaidVnd,
            calc.IsOverpaid,
            calc.NeedsRefund || hasApprovedRefundReturn,
            CatalogVersions.Encode(order.Version)));
    }

    public async Task<Result<RefundRecordedView>> Handle(CreateOrderRefund command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        if (command.RefundedAt > now)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("refunded_at_in_future", "Thời điểm hoàn tiền không được ở tương lai."));

        var bankRef = NormalizeOptional(command.BankReference);
        var trimmedReason = command.Reason.Trim();

        // L1: lock sales.orders to serialize all concurrent refunds and enforce all 10 financial invariants atomically.
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, expectedVersion: null, ct);
        if (loaded.Order is not { } order)
            return Result.Failure<RefundRecordedView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        if (order.IsBackfilled)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("backfilled_order_immutable", "Không thể hoàn tiền cho đơn lịch sử backfill."));

        // Invariant 1: shipping_amount_vnd bounds and allowed refund kinds (ck_refunds_shipping_amount_vnd, ck_refunds_kind_shipping).
        if (command.ShippingAmountVnd < 0 || command.ShippingAmountVnd > command.AmountVnd)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("invalid_shipping_refund_amount", "Phí vận chuyển hoàn phải nằm trong khoảng từ 0 đến tổng số tiền hoàn."));

        if (!RefundKind.AllowsShippingRefund(command.Kind) && command.ShippingAmountVnd > 0)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("shipping_refund_not_allowed", "Chỉ hoàn tiền loại return hoặc adjustment mới được hoàn phí vận chuyển."));

        // Invariant 2: (kind = 'return') = (return_request_id IS NOT NULL) (ck_refunds_kind_return_request).
        if (command.Kind == RefundKind.Return && command.ReturnRequestId is null)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("return_request_required", "Hoàn tiền loại return bắt buộc gắn mã yêu cầu đổi trả."));

        if (command.Kind != RefundKind.Return && command.ReturnRequestId is not null)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("return_request_not_allowed", "Chỉ hoàn tiền loại return mới được gắn mã yêu cầu đổi trả."));

        // Invariant 3: order status matrix per refund kind (BR-PAY-07).
        var validStatusForKind = command.Kind switch
        {
            RefundKind.Overpayment => order.Status is not (OrderStatus.Cancelled or OrderStatus.DeliveryFailed),
            RefundKind.Cancellation => order.Status is OrderStatus.Cancelled or OrderStatus.DeliveryFailed,
            RefundKind.Adjustment => order.Status == OrderStatus.Delivered,
            RefundKind.Return => order.Status == OrderStatus.Delivered,
            _ => false
        };
        if (!validStatusForKind)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("invalid_refund_kind_for_order_status", "Loại hoàn tiền không hợp lệ với trạng thái hiện tại của đơn hàng."));

        // L2: read committed payments + classified refund ledger under L1 lock.
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var currentNet = checked(ledger.ConfirmedPaidVnd - ledger.RefundedVnd);
        var goodsAmountVnd = checked(command.AmountVnd - command.ShippingAmountVnd);
        var netMerchandiseVnd = checked(order.SubtotalVnd - order.DiscountVnd);

        // Invariant 4: global net ceiling (BR-PAY-07).
        if (command.AmountVnd > currentNet)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("refund_exceeds_net_paid", "Số tiền hoàn vượt quá số tiền thực thu ròng hiện có của đơn hàng."));

        // Invariant 5: cumulative shipping refund ceiling across all refunds on the order.
        if (command.ShippingAmountVnd > 0 && checked(ledger.ShippingRefundedVnd + command.ShippingAmountVnd) > order.ShippingFeeVnd)
            return Result.Failure<RefundRecordedView>(Error.BusinessRule("refund_exceeds_shipping_fee", "Tổng phí vận chuyển hoàn vượt quá phí vận chuyển của đơn hàng."));

        // Invariant 6: overpayment ceiling = min(currentNet, max(0, effectivePaid - total)).
        if (command.Kind == RefundKind.Overpayment)
        {
            var effectivePaid = checked(ledger.ConfirmedPaidVnd - ledger.OverpaymentRefundedVnd);
            var rawOverpaid = effectivePaid > order.TotalVnd ? checked(effectivePaid - order.TotalVnd) : 0L;
            var maxOverpaymentRefund = Math.Min(currentNet, rawOverpaid);
            if (command.AmountVnd > maxOverpaymentRefund)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("refund_exceeds_overpayment", "Số tiền hoàn thu thừa vượt quá phần thu thừa hiện có của đơn hàng."));
        }

        // Invariant 7: adjustment dual ceiling (remaining merchandise goods + shipping fee already checked above).
        if (command.Kind == RefundKind.Adjustment)
        {
            var priorCommercialGoods = checked(ledger.ReturnGoodsRefundedVnd + ledger.AdjustmentGoodsRefundedVnd);
            var remainingMerchandise = netMerchandiseVnd > priorCommercialGoods
                ? checked(netMerchandiseVnd - priorCommercialGoods)
                : 0L;
            if (goodsAmountVnd > remainingMerchandise)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("refund_exceeds_order_merchandise", "Phần hoàn tiền hàng vượt quá giá trị hàng hóa còn lại của đơn hàng."));
        }

        // Invariant 8 & 9: return coordination (L3 lock on return_requests, fail-closed empty lines, cumulative half-up ceiling, full-return shipping rule).
        string? returnRequestStatus = null;
        if (command.Kind == RefundKind.Return)
        {
            var returnRequestId = command.ReturnRequestId!.Value;
            var returnRequest = await returnRefunds.LoadForUpdateAsync(order.Id, returnRequestId, ct);
            if (returnRequest is null)
                return Result.Failure<RefundRecordedView>(new("not_found", "Không tìm thấy yêu cầu đổi trả.", ErrorType.NotFound));

            if (returnRequest.OrderId != order.Id)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("return_request_order_mismatch", "Yêu cầu đổi trả không thuộc đơn hàng này."));

            if (returnRequest.Status != ReturnRequestStatus.Approved || returnRequest.Resolution != ReturnResolution.Refund)
                return Result.Failure<RefundRecordedView>(new("invalid_return_status", "Yêu cầu đổi trả chưa ở trạng thái đã duyệt hoàn tiền hoặc đã xử lý xong.", ErrorType.Conflict));

            if (returnRequest.Lines.Count == 0)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("return_request_has_no_lines", "Phiếu đổi trả hoàn tiền phải có ít nhất một dòng hàng."));

            var priorQtyByLine = await returnRefunds.GetPriorRefundedQuantitiesByOrderLineAsync(order.Id, returnRequestId, ct);

            ReturnRefundCeilingResult ceiling;
            try
            {
                ceiling = OrderPaymentStatusCalculator.CalculateReturnRefundCeiling(
                    order,
                    returnRequest.Lines,
                    priorQtyByLine,
                    ledger.ReturnGoodsRefundedVnd,
                    ledger.AdjustmentGoodsRefundedVnd);
            }
            catch (DomainException ex)
            {
                return Result.Failure<RefundRecordedView>(Error.BusinessRule(ex.Code, ex.Message));
            }

            if (goodsAmountVnd > ceiling.MaxGoodsRefundVnd)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("refund_exceeds_return_lines", "Phần hoàn tiền hàng vượt quá trần lũy kế của các dòng đổi trả sau phân bổ giảm giá."));

            if (command.ShippingAmountVnd > 0 && !ceiling.IsFullRefundReturnCompleted)
                return Result.Failure<RefundRecordedView>(Error.BusinessRule("shipping_refund_requires_full_return", "Chỉ được hoàn phí vận chuyển khi đơn hàng đã hoàn tất trả 100% mọi dòng qua phiếu hoàn tiền."));
        }

        // R-P201-06: append-only insert into sales.refunds with SAVEPOINT catching 23505 on ux_refunds_return_request.
        var refundId = Guid.CreateVersion7();
        var inserted = await refunds.TryInsertWithSavepointAsync(new NewRefundRecord(
            refundId,
            order.Id,
            command.ReturnRequestId,
            command.Kind,
            command.AmountVnd,
            command.ShippingAmountVnd,
            command.Method,
            bankRef,
            trimmedReason,
            command.RefundedAt,
            command.ActorStaffId,
            now), ct);

        if (!inserted)
            return Result.Failure<RefundRecordedView>(new("return_already_refunded", "Yêu cầu đổi trả này đã có chứng từ hoàn tiền trước đó.", ErrorType.Conflict));

        if (command.ReturnRequestId is { } resolvedReturnId)
        {
            await returnRefunds.MarkResolvedAsync(resolvedReturnId, now, ct);
            returnRequestStatus = ReturnRequestStatus.Resolved;
        }

        var newRefunded = checked(ledger.RefundedVnd + command.AmountVnd);
        var newOverpaymentRefunded = command.Kind == RefundKind.Overpayment
            ? checked(ledger.OverpaymentRefundedVnd + command.AmountVnd)
            : ledger.OverpaymentRefundedVnd;
        var calc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            newRefunded,
            newOverpaymentRefunded,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(calc, now);
        await orders.SaveUpdatedOrderAsync(order, null, ct);

        // Invariant 10: compute NeedsRefund including any remaining approved refund return requests under L1 lock.
        var hasOtherApprovedRefundReturn = await returnRefunds.HasOtherApprovedRefundReturnAsync(
            order.Id,
            command.ReturnRequestId,
            ct);
        var effectiveNeedsRefund = calc.NeedsRefund || hasOtherApprovedRefundReturn;

        foreach (var hook in refundHooks)
            await hook.OnRefundRecordedAsync(
                order,
                refundId,
                command.ReturnRequestId,
                command.Kind,
                command.AmountVnd,
                command.ShippingAmountVnd,
                calc,
                ct);

        return Result.Success(new RefundRecordedView(
            refundId,
            order.Id,
            order.Code,
            command.ReturnRequestId,
            returnRequestStatus,
            command.Kind,
            command.Method,
            command.AmountVnd,
            command.ShippingAmountVnd,
            command.RefundedAt,
            order.Status,
            order.PaymentStatus,
            calc.ConfirmedPaidVnd,
            calc.RefundedVnd,
            calc.NetPaidVnd,
            calc.IsOverpaid,
            effectiveNeedsRefund,
            CatalogVersions.Encode(order.Version)));
    }

    public async Task<Result<AwaitingPaymentsPage>> Handle(GetAwaitingPayments query, CancellationToken cancellationToken)
    {
        var carrier = NormalizeOptional(query.Carrier);
        var page = await queries.GetAwaitingAsync(query.Type, carrier, query.Page, query.PageSize, cancellationToken);
        return Result.Success(page);
    }

    private void EnqueueEvents(Order order)
    {
        foreach (var evt in order.DrainEvents())
        {
            var payload = new OrderOutboxPayload(evt.OrderId, evt.OrderCode, evt.Status, evt.OccurredAt, evt.ReasonCode);
            outbox.Enqueue(evt.EventType, OrderOutboxMessage.Serialize(payload));
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
