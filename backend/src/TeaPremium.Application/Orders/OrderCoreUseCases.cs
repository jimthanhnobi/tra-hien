using FluentValidation;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record OrderLineRequest(
    Guid VariantId,
    int Quantity,
    long? OverrideUnitPriceVnd = null,
    string? PriceOverrideReason = null);

public sealed record CreateWebOrder(
    Guid CustomerId,
    Guid? CustomerAccountId,
    string PaymentMethod,
    OrderContactSnapshot Contact,
    IReadOnlyList<OrderLineRequest> Lines,
    string? VoucherCode,
    string? ReferralCode,
    long? ExpectedTotalVnd,
    OrderAttributionSnapshot Attribution,
    string? CustomerNote) : ICommand<OrderSummary>;

public sealed record CreateAdminOrder(
    string SalesChannel,
    Guid CustomerId,
    Guid CreatedByStaffId,
    bool ActorHasCostsRead,
    string PaymentMethod,
    OrderContactSnapshot Contact,
    IReadOnlyList<OrderLineRequest> Lines,
    string? VoucherCode,
    string? ReferralCode,
    long? ManualDiscountVnd,
    string? DiscountNote,
    long? OverrideShippingFeeVnd,
    OrderAttributionSnapshot Attribution,
    string? CustomerNote,
    string? InternalNote,
    bool ConfirmNow = false,
    bool CodCallConfirmed = false,
    string? ConfirmNote = null) : ICommand<OrderSummary>;

public sealed record ConfirmOrder(
    Guid OrderId,
    uint? ExpectedVersion,
    string ActorType,
    Guid? ActorId,
    bool CodCallConfirmed = false,
    string? Note = null) : ICommand<OrderSummary>;

public sealed record CancelOrder(
    Guid OrderId,
    uint? ExpectedVersion,
    string ActorType,
    Guid? ActorId,
    string? Reason) : ICommand<OrderSummary>;

public sealed record UpdateOrderLines(
    Guid OrderId,
    uint ExpectedVersion,
    bool ActorHasCostsRead,
    IReadOnlyList<OrderLineRequest> Lines,
    string? VoucherCode,
    string? ReferralCode,
    long? ManualDiscountVnd,
    string? DiscountNote,
    long? OverrideShippingFeeVnd) : ICommand<OrderSummary>;

public sealed record MarkOrderShipping(
    Guid OrderId,
    uint? ExpectedVersion,
    Guid StaffId,
    string? Note = null) : ICommand<OrderSummary>;

public sealed record MarkOrderDelivered(
    Guid OrderId,
    uint? ExpectedVersion,
    string ActorType,
    Guid? ActorId,
    DateTimeOffset DeliveredAt,
    string? Note = null) : ICommand<OrderSummary>;

public sealed record MarkOrderDeliveryFailed(
    Guid OrderId,
    uint? ExpectedVersion,
    string ActorType,
    Guid? ActorId,
    string Reason) : ICommand<OrderSummary>;

public sealed class CreateWebOrderValidator : AbstractValidator<CreateWebOrder>
{
    public CreateWebOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.PaymentMethod).Must(OrderPaymentMethod.IsValid);
        RuleFor(x => x.Contact).NotNull();
        RuleFor(x => x.Attribution).NotNull();
        RuleFor(x => x.Lines).NotNull().NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

public sealed class CreateAdminOrderValidator : AbstractValidator<CreateAdminOrder>
{
    public CreateAdminOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.CreatedByStaffId).NotEmpty();
        RuleFor(x => x.PaymentMethod).Must(OrderPaymentMethod.IsValid);
        RuleFor(x => x.Contact).NotNull();
        RuleFor(x => x.Attribution).NotNull();
        RuleFor(x => x.Lines).NotNull().NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 1000);
        });
    }
}

public sealed class ConfirmOrderValidator : AbstractValidator<ConfirmOrder>
{
    public ConfirmOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorType).Must(OrderActorType.IsValid);
    }
}

public sealed class CancelOrderValidator : AbstractValidator<CancelOrder>
{
    public CancelOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorType).Must(OrderActorType.IsValid);
    }
}

public sealed class UpdateOrderLinesValidator : AbstractValidator<UpdateOrderLines>
{
    public UpdateOrderLinesValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Lines).NotNull().NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 1000);
        });
    }
}

public sealed class MarkOrderShippingValidator : AbstractValidator<MarkOrderShipping>
{
    public MarkOrderShippingValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.StaffId).NotEmpty();
    }
}

public sealed class MarkOrderDeliveredValidator : AbstractValidator<MarkOrderDelivered>
{
    public MarkOrderDeliveredValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorType).Must(OrderActorType.IsValid);
    }
}

public sealed class MarkOrderDeliveryFailedValidator : AbstractValidator<MarkOrderDeliveryFailed>
{
    public MarkOrderDeliveryFailedValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ActorType).Must(OrderActorType.IsValid);
        RuleFor(x => x.Reason).NotEmpty();
    }
}

public sealed class OrderCoreHandlers(
    IOrderRepository orders,
    IInventoryReservationService reservations,
    IVoucherRedemptionPort vouchers,
    IOrderPricingRead pricingRead,
    IOrderPaymentLedgerReader paymentLedger,
    IOutboxWriter outbox,
    IClock clock,
    IEnumerable<IOrderDeliveredHook> deliveredHooks) :
    ICommandHandler<CreateWebOrder, OrderSummary>,
    ICommandHandler<CreateAdminOrder, OrderSummary>,
    ICommandHandler<ConfirmOrder, OrderSummary>,
    ICommandHandler<CancelOrder, OrderSummary>,
    ICommandHandler<UpdateOrderLines, OrderSummary>,
    ICommandHandler<MarkOrderShipping, OrderSummary>,
    ICommandHandler<MarkOrderDelivered, OrderSummary>,
    ICommandHandler<MarkOrderDeliveryFailed, OrderSummary>
{
    public async Task<Result<OrderSummary>> Handle(CreateWebOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var settings = await pricingRead.GetOrderSettingsAsync(ct);

        // Lock products -> variants -> stock_levels BEFORE inserting sales.orders / sales.order_lines
        // so FK KEY SHARE on order_lines never deadlocks with FOR UPDATE on variants (AC-P2-01).
        var variantIds = command.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var locked = await reservations.LockVariantsForOrderAsync(variantIds, now, ct);

        var calcLines = BuildCheckoutLines(command.Lines, locked, allowOverride: false);
        if (calcLines is null)
            return Result.Failure<OrderSummary>(new("cart_has_unavailable_items", "Giỏ hàng có biến thể không tồn tại hoặc không bán được.", ErrorType.Conflict));

        var discountInput = await BuildLockedDiscountInputForCreateAsync(
            command.VoucherCode, command.ReferralCode, null, null, false, command.CustomerId, ct);

        var calc = CheckoutCalculator.Calculate(new(
            CheckoutMode.WebPlaceOrder,
            now,
            calcLines,
            discountInput,
            new(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd),
            new(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent)));

        if (!calc.IsSuccess)
            return Result.Failure<OrderSummary>(MapCalculationError(calc));

        if (command.ExpectedTotalVnd is { } expected && expected != calc.TotalVnd)
            return Result.Failure<OrderSummary>(new("price_changed", "Giá hoặc phí vận chuyển đơn hàng đã thay đổi.", ErrorType.Conflict));

        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        var placeholderCode = OrderCode.Format(vietnamDate, "2222");
        var orderId = Guid.CreateVersion7();
        var orderLines = calc.PricedLines.Select(ToOrderLineItem).ToArray();
        DateTimeOffset? paymentDueAt = command.PaymentMethod == OrderPaymentMethod.BankTransfer
            ? now.AddHours(settings.BankTransferHoldHours)
            : null;

        var order = Order.PlaceWeb(
            orderId,
            placeholderCode,
            command.CustomerId,
            command.CustomerAccountId,
            command.PaymentMethod,
            paymentDueAt,
            command.Contact,
            orderLines,
            calc.SubtotalVnd,
            calc.DiscountVnd,
            calc.ShippingFeeVnd,
            calc.TotalVnd,
            calc.AppliedVoucherId,
            null,
            command.Attribution,
            command.CustomerNote,
            now);

        var assignedCode = await orders.InsertAsync(order, vietnamDate, ct);
        if (assignedCode is null)
            return Result.Failure<OrderSummary>(new("order_code_exhausted", "Không thể cấp mã đơn hàng duy nhất.", ErrorType.Conflict));

        var reservationLines = order.Lines
            .Select(l => new OrderReservationLine(l.Id, l.VariantId, l.ProductId, l.Sku, l.Quantity))
            .ToArray();
        var reserveResult = await reservations.ReserveForOrderAsync(reservationLines, ct);
        if (!reserveResult.IsSuccess)
            return Result.Failure<OrderSummary>(MapReservationError(reserveResult));

        if (calc.AppliedVoucherId is { } voucherId)
        {
            var applied = await vouchers.UpsertApplyAsync(voucherId, order.Id, order.CustomerId, calc.DiscountVnd, ct);
            if (!applied.IsSuccess)
                return Result.Failure<OrderSummary>(Error.BusinessRule(applied.ErrorCode!, applied.ErrorMessage!));
        }

        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(CreateAdminOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var settings = await pricingRead.GetOrderSettingsAsync(ct);

        var variantIds = command.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var locked = await reservations.LockVariantsForOrderAsync(variantIds, now, ct);

        var calcLines = BuildCheckoutLines(command.Lines, locked, allowOverride: true);
        if (calcLines is null)
            return Result.Failure<OrderSummary>(Error.BusinessRule("variant_not_sellable", "Biến thể không tồn tại hoặc không bán được."));

        var discountInput = await BuildLockedDiscountInputForCreateAsync(
            command.VoucherCode, command.ReferralCode, command.ManualDiscountVnd, command.DiscountNote,
            command.ActorHasCostsRead, command.CustomerId, ct);

        var calc = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder,
            now,
            calcLines,
            discountInput,
            new(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd, command.OverrideShippingFeeVnd),
            new(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent)));

        if (!calc.IsSuccess)
            return Result.Failure<OrderSummary>(MapCalculationError(calc));

        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        var placeholderCode = OrderCode.Format(vietnamDate, "2222");
        var orderId = Guid.CreateVersion7();
        var orderLines = calc.PricedLines.Select(ToOrderLineItem).ToArray();

        var order = Order.CreateAdmin(
            orderId,
            placeholderCode,
            command.SalesChannel,
            command.CustomerId,
            command.CreatedByStaffId,
            command.PaymentMethod,
            command.Contact,
            orderLines,
            calc.SubtotalVnd,
            calc.DiscountVnd,
            calc.ShippingFeeVnd,
            calc.TotalVnd,
            calc.AppliedVoucherId,
            command.ManualDiscountVnd is > 0 ? command.DiscountNote : null,
            command.Attribution,
            command.CustomerNote,
            command.InternalNote,
            now,
            command.ConfirmNow,
            settings.CodRequiresCall,
            command.CodCallConfirmed,
            command.ConfirmNote);

        var assignedCode = await orders.InsertAsync(order, vietnamDate, ct);
        if (assignedCode is null)
            return Result.Failure<OrderSummary>(new("order_code_exhausted", "Không thể cấp mã đơn hàng duy nhất.", ErrorType.Conflict));

        var reservationLines = order.Lines
            .Select(l => new OrderReservationLine(l.Id, l.VariantId, l.ProductId, l.Sku, l.Quantity))
            .ToArray();
        var reserveResult = await reservations.ReserveForOrderAsync(reservationLines, ct);
        if (!reserveResult.IsSuccess)
            return Result.Failure<OrderSummary>(MapReservationError(reserveResult));

        if (calc.AppliedVoucherId is { } voucherId)
        {
            var applied = await vouchers.UpsertApplyAsync(voucherId, order.Id, order.CustomerId, calc.DiscountVnd, ct);
            if (!applied.IsSuccess)
                return Result.Failure<OrderSummary>(Error.BusinessRule(applied.ErrorCode!, applied.ErrorMessage!));
        }

        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(ConfirmOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        // BR-PAY-02 (O3a=C): self-heal/recalculate payment_status from ledger under L1 lock (handles legacy 0-VND rows).
        if (!order.IsBackfilled)
        {
            var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
            var paymentCalc = OrderPaymentStatusCalculator.Calculate(
                ledger.ConfirmedPaidVnd,
                ledger.RefundedVnd,
                ledger.OverpaymentRefundedVnd,
                order.TotalVnd,
                order.Status,
                order.IsBackfilled);
            order.ApplyPaymentCalculation(paymentCalc, now);
        }

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        order.Confirm(command.ActorType, command.ActorId, now, settings.CodRequiresCall, command.CodCallConfirmed, command.Note);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(CancelOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        var hasActiveShipment = order.Status == OrderStatus.Confirmed
            && await orders.HasActiveOrderShipmentAsync(order.Id, ct);
        order.Cancel(command.ActorType, command.ActorId, command.Reason, now, hasActiveShipment);

        // BR-PAY-02 (O3a=C): recalculate payment_status using cancelled precedence in the same transaction.
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var paymentCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(paymentCalc, now);

        var closeReason = command.ActorType == OrderActorType.System
            && string.Equals(command.Reason?.Trim(), "payment_expired", StringComparison.Ordinal)
                ? ReservationCloseReason.PaymentExpired
                : ReservationCloseReason.OrderCancelled;

        await reservations.ReleaseForOrderAsync(order.Id, closeReason, ct);
        await vouchers.RevertForOrderAsync(order.Id, ct);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(UpdateOrderLines command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        if (!OrderStatus.AllowsLineMutation(order.Status))
            return Result.Failure<OrderSummary>(new("invalid_status_transition", "Chỉ được sửa hoặc xóa dòng hàng khi đơn đang chờ hoặc đã xác nhận.", ErrorType.Conflict));

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        var allVariantIds = order.Lines.Select(x => x.VariantId)
            .Concat(command.Lines.Select(x => x.VariantId))
            .Distinct()
            .ToArray();
        // L2 -> L3 -> L4: lock products -> variants -> stock_levels before L5 (pricing.vouchers).
        var locked = await reservations.LockVariantsForOrderAsync(allVariantIds, now, ct);

        var calcLines = BuildCheckoutLines(command.Lines, locked, allowOverride: true);
        if (calcLines is null)
            return Result.Failure<OrderSummary>(Error.BusinessRule("variant_not_sellable", "Biến thể không tồn tại hoặc không bán được."));

        // F1 / G12=A / R-P201-02: lock existing applied voucher and/or requested voucher in ascending id order (L5).
        var existingRedemption = await vouchers.GetByOrderIdAsync(order.Id, ct);
        var existingAppliedVoucherId = existingRedemption is { Status: "applied" } ? existingRedemption.VoucherId : (Guid?)null;
        var normalizedVoucherCode = string.IsNullOrWhiteSpace(command.VoucherCode) ? null : command.VoucherCode.Trim().ToUpperInvariant();
        var requestedLookup = normalizedVoucherCode is null ? null : await vouchers.GetByCodeAsync(normalizedVoucherCode, ct);

        var voucherIdsToLock = new List<Guid>(2);
        if (existingAppliedVoucherId is { } prevId) voucherIdsToLock.Add(prevId);
        if (requestedLookup is not null) voucherIdsToLock.Add(requestedLookup.Id);
        var lockedVouchers = await vouchers.LockByIdsAsync(voucherIdsToLock, ct);

        VoucherSnapshot? requestedLockedVoucher = requestedLookup is not null && lockedVouchers.TryGetValue(requestedLookup.Id, out var lockedVoucherRow)
            ? lockedVoucherRow
            : null;
        var keepingSameAppliedVoucher = existingAppliedVoucherId is { } activeId
            && requestedLockedVoucher is not null
            && requestedLockedVoucher.Id == activeId;

        VoucherDiscountInput? voucherInput = null;
        if (normalizedVoucherCode is not null)
        {
            if (requestedLockedVoucher is null)
            {
                voucherInput = new(normalizedVoucherCode, null, order.CustomerId, 0);
            }
            else
            {
                // When retaining the voucher already applied to this order, exclude this order's own slot
                // from UsedCount and CustomerAppliedCount so per_customer_limit=1 / usage_limit do not false-fail.
                var snapshotForCalc = keepingSameAppliedVoucher
                    ? requestedLockedVoucher with { UsedCount = Math.Max(0, requestedLockedVoucher.UsedCount - 1) }
                    : requestedLockedVoucher;
                var appliedByCustomer = await vouchers.CountAppliedByCustomerAsync(
                    requestedLockedVoucher.Id,
                    order.CustomerId,
                    excludeOrderId: order.Id,
                    ct);
                voucherInput = new(normalizedVoucherCode, snapshotForCalc, order.CustomerId, appliedByCustomer);
            }
        }

        AdminManualDiscountInput? manual = command.ManualDiscountVnd is { } manualAmount
            ? new(manualAmount, command.DiscountNote, command.ActorHasCostsRead)
            : null;

        var shippingRule = new ShippingRuleInput(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd, command.OverrideShippingFeeVnd);
        var limits = new CheckoutLimits(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent);
        var discountInput = new CheckoutDiscountInput(
            voucherInput,
            string.IsNullOrWhiteSpace(command.ReferralCode) ? null : command.ReferralCode.Trim(),
            manual);

        var calc = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder,
            now,
            calcLines,
            discountInput,
            shippingRule,
            limits));

        // BR-ORD-08 / G12=A / GAP-P205-01=A: when editing lines drops an ALREADY-APPLIED voucher
        // below min_order_vnd (or discount == 0), automatically drop the voucher and revert its redemption.
        if (!calc.IsSuccess && keepingSameAppliedVoucher && calc.ErrorCode == "voucher_min_order")
        {
            calc = CheckoutCalculator.Calculate(new(
                CheckoutMode.AdminOrder,
                now,
                calcLines,
                CheckoutDiscountInput.None,
                shippingRule,
                limits));
        }

        if (!calc.IsSuccess)
            return Result.Failure<OrderSummary>(MapCalculationError(calc));

        var nextItems = calc.PricedLines.Select(ToOrderLineItem).ToArray();
        var delta = order.ReplaceLines(
            nextItems,
            calc.SubtotalVnd,
            calc.DiscountVnd,
            calc.ShippingFeeVnd,
            calc.TotalVnd,
            calc.AppliedVoucherId,
            command.ManualDiscountVnd is > 0 ? command.DiscountNote : null,
            now);

        // BR-ORD-08 / BR-PAY-02: recalculate payment_status from committed ledger whenever order total changes.
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var paymentCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(paymentCalc, now);

        var syncResult = await reservations.SyncOrderLinesAsync(
            order.Id,
            delta,
            innerCt => orders.SaveUpdatedOrderAsync(order, delta, innerCt),
            ct);
        if (!syncResult.IsSuccess)
            return Result.Failure<OrderSummary>(MapReservationError(syncResult));

        // Synchronize pricing.vouchers and pricing.voucher_redemptions in the same transaction (G12=A / R-P201-02).
        if (calc.AppliedVoucherId is null)
        {
            if (existingAppliedVoucherId is not null)
                await vouchers.RevertForOrderAsync(order.Id, ct);
        }
        else if (existingAppliedVoucherId == calc.AppliedVoucherId)
        {
            await vouchers.UpdateAppliedAmountAsync(order.Id, calc.DiscountVnd, ct);
        }
        else
        {
            if (existingAppliedVoucherId is not null)
                await vouchers.RevertForOrderAsync(order.Id, ct);

            var applied = await vouchers.UpsertApplyAsync(calc.AppliedVoucherId.Value, order.Id, order.CustomerId, calc.DiscountVnd, ct);
            if (!applied.IsSuccess)
                return Result.Failure<OrderSummary>(Error.BusinessRule(applied.ErrorCode!, applied.ErrorMessage!));
        }

        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(MarkOrderShipping command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        // BR-PAY-02 (O3a=C): self-heal/recalculate payment_status from ledger under L1 lock (handles legacy 0-VND rows).
        if (!order.IsBackfilled)
        {
            var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
            var paymentCalc = OrderPaymentStatusCalculator.Calculate(
                ledger.ConfirmedPaidVnd,
                ledger.RefundedVnd,
                ledger.OverpaymentRefundedVnd,
                order.TotalVnd,
                order.Status,
                order.IsBackfilled);
            order.ApplyPaymentCalculation(paymentCalc, now);
        }

        var hasOrderShipment = await orders.HasOrderShipmentAsync(order.Id, ct);
        order.MarkShipping(command.StaffId, now, hasOrderShipment, command.Note);

        var consumed = await reservations.ConsumeForOrderAsync(order.Id, order.Lines, OrderActorType.Staff, command.StaffId, ct);
        if (!consumed.IsSuccess)
            return Result.Failure<OrderSummary>(new(consumed.ErrorCode!, consumed.ErrorMessage!, ErrorType.Conflict));

        order.LockLineUnitCosts(consumed.UnitCostByLineId);
        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(MarkOrderDelivered command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        order.MarkDelivered(command.ActorType, command.ActorId, command.DeliveredAt, clock.UtcNow, command.Note);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        foreach (var hook in deliveredHooks)
            await hook.OnOrderDeliveredAsync(order, ct);

        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    public async Task<Result<OrderSummary>> Handle(MarkOrderDeliveryFailed command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        order.MarkDeliveryFailed(command.ActorType, command.ActorId, command.Reason, now);

        // BR-SHP-03 / BR-PAY-02 (O1=A / O3a=C): recalculate payment_status using delivery_failed precedence in the same transaction.
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var paymentCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(paymentCalc, now);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return Result.Success(OrderSummary.From(order));
    }

    private static List<CheckoutLineInput>? BuildCheckoutLines(
        IReadOnlyList<OrderLineRequest> requested,
        IReadOnlyDictionary<Guid, LockedOrderVariant> locked,
        bool allowOverride)
    {
        var result = new List<CheckoutLineInput>(requested.Count);
        foreach (var line in requested)
        {
            if (!locked.TryGetValue(line.VariantId, out var variant))
                return null;
            result.Add(new(
                variant.VariantId,
                variant.ProductId,
                variant.Sku,
                variant.ProductName,
                variant.VariantName,
                variant.ProductActive,
                variant.VariantActive,
                variant.PriceVnd,
                variant.Promotions,
                variant.IsUniquePiece,
                variant.Available,
                line.Quantity,
                allowOverride ? line.OverrideUnitPriceVnd : null,
                allowOverride ? line.PriceOverrideReason : null));
        }
        return result;
    }

    private async Task<CheckoutDiscountInput> BuildLockedDiscountInputForCreateAsync(
        string? voucherCode,
        string? referralCode,
        long? manualDiscountVnd,
        string? discountNote,
        bool actorHasCostsRead,
        Guid customerId,
        CancellationToken ct)
    {
        VoucherDiscountInput? voucherInput = null;
        if (!string.IsNullOrWhiteSpace(voucherCode))
        {
            var normalizedCode = voucherCode.Trim().ToUpperInvariant();
            // F2: acquire L5 lock (pricing.vouchers FOR NO KEY UPDATE) BEFORE counting per-customer redemptions
            // so concurrent checkouts from the same customer cannot bypass per_customer_limit.
            var snapshot = await vouchers.LockByCodeAsync(normalizedCode, ct);
            var usedByCustomer = snapshot is null
                ? 0
                : await vouchers.CountAppliedByCustomerAsync(snapshot.Id, customerId, excludeOrderId: null, ct);
            voucherInput = new(normalizedCode, snapshot, customerId, usedByCustomer);
        }

        AdminManualDiscountInput? manual = manualDiscountVnd is { } amount
            ? new(amount, discountNote, actorHasCostsRead)
            : null;

        return new(voucherInput, string.IsNullOrWhiteSpace(referralCode) ? null : referralCode.Trim(), manual);
    }

    private static OrderLineItem ToOrderLineItem(PricedCheckoutLine priced) =>
        OrderLineItem.Create(
            Guid.CreateVersion7(),
            priced.VariantId,
            priced.ProductId,
            priced.Sku,
            priced.ProductName,
            priced.VariantName,
            priced.Quantity,
            priced.ListPriceVnd,
            priced.UnitPriceVnd,
            priced.PriceSource,
            priced.PriceOverrideReason);

    private void EnqueueEvents(Order order)
    {
        foreach (var evt in order.DrainEvents())
        {
            var payload = new OrderOutboxPayload(evt.OrderId, evt.OrderCode, evt.Status, evt.OccurredAt, evt.ReasonCode);
            outbox.Enqueue(evt.EventType, OrderOutboxMessage.Serialize(payload));
        }
    }

    private static Error MapCalculationError(CheckoutCalculationResult calc)
    {
        var code = calc.ErrorCode ?? "validation_failed";
        var message = calc.ErrorMessage ?? "Không thể tính đơn hàng.";
        if (code == "out_of_stock")
            return OutOfStockErrorFormatting.CreateError(message, calc.OutOfStockItems);
        var type = code switch
        {
            "validation_failed" => ErrorType.Validation,
            "cart_has_unavailable_items" or "unique_piece_violation" or "price_changed" or "invalid_status_transition" => ErrorType.Conflict,
            _ => ErrorType.BusinessRule
        };
        return new(code, message, type);
    }

    private static Error MapReservationError(ReservationResult result)
    {
        var code = result.ErrorCode ?? "out_of_stock";
        var message = result.ErrorMessage ?? "Không đủ tồn kho để giữ hàng.";
        if (code == "out_of_stock")
            return OutOfStockErrorFormatting.CreateError(message, result.OutOfStockItems);
        var type = code is "unique_piece_violation" or "invalid_status_transition"
            ? ErrorType.Conflict
            : ErrorType.BusinessRule;
        return new(code, message, type);
    }

    private static Result<OrderSummary> NotFoundFailure() =>
        Result.Failure<OrderSummary>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

    private static Result<OrderSummary> ConcurrencyFailure() =>
        Result.Failure<OrderSummary>(new("concurrency_conflict", "Đơn hàng đã được cập nhật bởi thao tác khác; tải lại trước khi lưu.", ErrorType.Conflict));
}
