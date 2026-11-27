using FluentValidation;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm.Contracts;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record AdminOrderNewCustomerInput(string FullName, string Phone);

public sealed record AdminOrderLineInput(
    Guid VariantId,
    int Quantity,
    long? UnitPriceVnd = null,
    string? PriceOverrideReason = null);

public sealed record AdminOrderShippingInput(
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string? WardCode,
    string? WardNameText,
    string AddressLine,
    string? CustomerNote);

public sealed record PatchAdminOrderShippingInput(
    bool HasReceiverName,
    string? ReceiverName,
    bool HasReceiverPhone,
    string? ReceiverPhone,
    bool HasProvinceCode,
    string? ProvinceCode,
    bool HasWardCode,
    string? WardCode,
    bool HasWardNameText,
    string? WardNameText,
    bool HasAddressLine,
    string? AddressLine,
    bool HasCustomerNote,
    string? CustomerNote);

public sealed record ListAdminOrders(
    int Page,
    int PageSize,
    string? Sort,
    string? Status,
    string? PaymentStatus,
    string? Channel,
    string? Origin,
    string? From,
    string? To,
    string? Q,
    Guid? CustomerId,
    string? Flag) : IQuery<CatalogPage<AdminOrderListItemView>>;

public sealed record GetAdminOrderDetail(Guid OrderId, OrderActor Actor) : IQuery<AdminOrderDetailView>;

public sealed record CreateAdminOrderHttp(
    Guid? CustomerId,
    AdminOrderNewCustomerInput? NewCustomer,
    string SalesChannel,
    string PaymentMethod,
    IReadOnlyList<AdminOrderLineInput> Lines,
    long? DiscountVnd,
    string? DiscountNote,
    long? ShippingFeeVnd,
    AdminOrderShippingInput Shipping,
    string? InternalNote,
    bool ConfirmNow,
    bool CallConfirmed,
    string? CallNote,
    bool IsBackfilled,
    DateTimeOffset? DeliveredAt,
    OrderActor Actor) : ICommand<AdminOrderCreatedView>;

public sealed record PatchAdminOrder(
    Guid OrderId,
    uint ExpectedVersion,
    bool HasLines,
    IReadOnlyList<AdminOrderLineInput>? Lines,
    bool HasShipping,
    PatchAdminOrderShippingInput? Shipping,
    bool HasDiscountVnd,
    long? DiscountVnd,
    bool HasDiscountNote,
    string? DiscountNote,
    bool HasShippingFeeVnd,
    long? ShippingFeeVnd,
    bool HasInternalNote,
    string? InternalNote,
    OrderActor Actor) : ICommand<AdminOrderDetailView>;

public sealed record ConfirmAdminOrder(
    Guid OrderId,
    uint ExpectedVersion,
    bool CallConfirmed,
    string? CallNote,
    string? Note,
    OrderActor Actor) : ICommand<AdminOrderDetailView>;

public sealed record CancelAdminOrder(
    Guid OrderId,
    uint ExpectedVersion,
    string Reason,
    OrderActor Actor) : ICommand<AdminOrderDetailView>;

public sealed record ReorderAdminOrder(
    Guid OrderId,
    string? SalesChannel,
    string? PaymentMethod,
    string? InternalNote,
    OrderActor Actor) : ICommand<AdminOrderDetailView>;

public sealed record GetAdminOrderPackingSlip(Guid OrderId, bool HidePrices) : IQuery<string>;

public sealed class ListAdminOrdersValidator : AbstractValidator<ListAdminOrders>
{
    public ListAdminOrdersValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Sort).Must(s => s is null || AdminOrderSortOptions.IsValid(s)).WithMessage("invalid_sort");
        RuleFor(x => x.Status).Must(s => s is null || OrderStatus.IsValid(s)).WithMessage("invalid_status");
        RuleFor(x => x.PaymentStatus).Must(s => s is null || OrderPaymentStatus.IsValid(s)).WithMessage("invalid_payment_status");
        RuleFor(x => x.Channel).Must(c => c is null || SalesChannel.IsValid(c)).WithMessage("invalid_channel");
        RuleFor(x => x.Origin).Must(o => o is null || OrderOrigin.IsValid(o)).WithMessage("invalid_origin");
        RuleFor(x => x.From).Must(v => AdminOrderFilterParser.TryParseFromFilter(v, out _)).WithMessage("invalid_from");
        RuleFor(x => x.To).Must(v => AdminOrderFilterParser.TryParseToFilter(v, out _, out _)).WithMessage("invalid_to");
        RuleFor(x => x.Q).MaximumLength(200).Must(CatalogInput.WithoutNul);
        RuleFor(x => x.CustomerId).Must(id => id is null || id.Value != Guid.Empty).WithMessage("invalid_customer_id");
        RuleFor(x => x.Flag).Must(f => f is null || AdminOrderFlags.IsValid(f)).WithMessage("invalid_flag");
    }
}

public sealed class GetAdminOrderDetailValidator : AbstractValidator<GetAdminOrderDetail>
{
    public GetAdminOrderDetailValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
    }
}

public sealed class CreateAdminOrderHttpValidator : AbstractValidator<CreateAdminOrderHttp>
{
    public CreateAdminOrderHttpValidator()
    {
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.CustomerId.HasValue ^ (x.NewCustomer is not null))
            .WithMessage("Phải truyền đúng một trong customerId hoặc newCustomer.");
        When(x => x.CustomerId.HasValue, () =>
        {
            RuleFor(x => x.CustomerId!.Value).NotEmpty();
        });
        When(x => x.NewCustomer is not null, () =>
        {
            RuleFor(x => x.NewCustomer!.FullName)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .Must(v => v is not null && v.Trim().Length is >= 1 and <= CustomerRules.MaxFullNameLength);
            RuleFor(x => x.NewCustomer!.Phone)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .MaximumLength(30);
        });
        RuleFor(x => x.SalesChannel).Must(SalesChannel.IsValidForAdmin).WithMessage("invalid_sales_channel");
        RuleFor(x => x.PaymentMethod).Must(OrderPaymentMethod.IsValid).WithMessage("invalid_payment_method");
        RuleFor(x => x.Lines).NotNull().NotEmpty();
        RuleFor(x => x.Lines)
            .Must(lines => lines is null || lines.Select(l => l.VariantId).Distinct().Count() == lines.Count)
            .WithMessage("duplicate_variant");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 1000);
            line.RuleFor(l => l.UnitPriceVnd).Must(p => p is null || p.Value >= 0);
            line.RuleFor(l => l.PriceOverrideReason).Must(CatalogInput.WithoutNul).MaximumLength(500);
            line.RuleFor(l => l).Must(l => l.UnitPriceVnd is null
                ? string.IsNullOrWhiteSpace(l.PriceOverrideReason)
                : !string.IsNullOrWhiteSpace(l.PriceOverrideReason) && l.PriceOverrideReason.Trim().Length >= 3)
                .WithMessage("invalid_price_override_reason");
        });
        RuleFor(x => x.DiscountVnd).Must(d => d is null || d.Value >= 0);
        RuleFor(x => x.DiscountNote).Must(CatalogInput.WithoutNul).MaximumLength(500);
        RuleFor(x => x).Must(x => x.DiscountVnd is not > 0 || !string.IsNullOrWhiteSpace(x.DiscountNote))
            .WithMessage("discount_note_required");
        RuleFor(x => x.ShippingFeeVnd).Must(f => f is null || f.Value >= 0);
        RuleFor(x => x.Shipping).NotNull();
        When(x => x.Shipping is not null, () =>
        {
            RuleFor(x => x.Shipping.ReceiverName)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .Must(v => v is not null && v.Trim().Length is >= 1 and <= 150);
            RuleFor(x => x.Shipping.ReceiverPhone)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .MaximumLength(30);
            RuleFor(x => x.Shipping.ProvinceCode)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .Must(v => v is not null && v.Trim().Length is >= 1 and <= 16);
            RuleFor(x => x.Shipping.WardCode).Must(CatalogInput.WithoutNul).MaximumLength(16);
            RuleFor(x => x.Shipping.WardNameText).Must(CatalogInput.WithoutNul).MaximumLength(150);
            RuleFor(x => x.Shipping)
                .Must(s => !string.IsNullOrWhiteSpace(s.WardCode) || !string.IsNullOrWhiteSpace(s.WardNameText))
                .WithMessage("ward_required");
            RuleFor(x => x.Shipping.AddressLine)
                .NotNull()
                .Must(CatalogInput.WithoutNul)
                .Must(v => v is not null && v.Trim().Length is >= 1 and <= 255);
            RuleFor(x => x.Shipping.CustomerNote).Must(CatalogInput.WithoutNul).MaximumLength(500);
        });
        RuleFor(x => x.InternalNote).Must(CatalogInput.WithoutNul).MaximumLength(2000);
        RuleFor(x => x.CallNote).Must(CatalogInput.WithoutNul).MaximumLength(500);
        RuleFor(x => x)
            .Must(x => !x.IsBackfilled || x.DeliveredAt.HasValue)
            .WithMessage("delivered_at_required_for_backfill");
        RuleFor(x => x)
            .Must(x => x.IsBackfilled || !x.DeliveredAt.HasValue)
            .WithMessage("delivered_at_only_allowed_for_backfill");
    }
}

public sealed class PatchAdminOrderValidator : AbstractValidator<PatchAdminOrder>
{
    public PatchAdminOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.HasLines || x.HasShipping || x.HasDiscountVnd || x.HasDiscountNote || x.HasShippingFeeVnd || x.HasInternalNote)
            .WithMessage("empty_patch");
        When(x => x.HasLines, () =>
        {
            RuleFor(x => x.Lines).NotNull().NotEmpty();
            RuleFor(x => x.Lines)
                .Must(lines => lines is null || lines.Select(l => l.VariantId).Distinct().Count() == lines.Count)
                .WithMessage("duplicate_variant");
            RuleForEach(x => x.Lines!).ChildRules(line =>
            {
                line.RuleFor(l => l.VariantId).NotEmpty();
                line.RuleFor(l => l.Quantity).InclusiveBetween(1, 1000);
                line.RuleFor(l => l.UnitPriceVnd).Must(p => p is null || p.Value >= 0);
                line.RuleFor(l => l.PriceOverrideReason).Must(CatalogInput.WithoutNul).MaximumLength(500);
                line.RuleFor(l => l).Must(l => l.UnitPriceVnd is null
                    ? string.IsNullOrWhiteSpace(l.PriceOverrideReason)
                    : !string.IsNullOrWhiteSpace(l.PriceOverrideReason) && l.PriceOverrideReason.Trim().Length >= 3)
                    .WithMessage("invalid_price_override_reason");
            });
        });
        When(x => x.HasShipping, () =>
        {
            RuleFor(x => x.Shipping).NotNull();
            When(x => x.Shipping is not null, () =>
            {
                RuleFor(x => x.Shipping!)
                    .Must(s => s.HasReceiverName || s.HasReceiverPhone || s.HasProvinceCode || s.HasWardCode
                        || s.HasWardNameText || s.HasAddressLine || s.HasCustomerNote)
                    .WithMessage("empty_shipping_patch");
                When(x => x.Shipping!.HasReceiverName, () =>
                    RuleFor(x => x.Shipping!.ReceiverName).NotNull().Must(CatalogInput.WithoutNul)
                        .Must(v => v is not null && v.Trim().Length is >= 1 and <= 150));
                When(x => x.Shipping!.HasReceiverPhone, () =>
                    RuleFor(x => x.Shipping!.ReceiverPhone).NotNull().Must(CatalogInput.WithoutNul).MaximumLength(30));
                When(x => x.Shipping!.HasProvinceCode, () =>
                    RuleFor(x => x.Shipping!.ProvinceCode).NotNull().Must(CatalogInput.WithoutNul)
                        .Must(v => v is not null && v.Trim().Length is >= 1 and <= 16));
                When(x => x.Shipping!.HasWardCode, () =>
                    RuleFor(x => x.Shipping!.WardCode).Must(CatalogInput.WithoutNul).MaximumLength(16));
                When(x => x.Shipping!.HasWardNameText, () =>
                    RuleFor(x => x.Shipping!.WardNameText).Must(CatalogInput.WithoutNul).MaximumLength(150));
                When(x => x.Shipping!.HasAddressLine, () =>
                    RuleFor(x => x.Shipping!.AddressLine).NotNull().Must(CatalogInput.WithoutNul)
                        .Must(v => v is not null && v.Trim().Length is >= 1 and <= 255));
                When(x => x.Shipping!.HasCustomerNote, () =>
                    RuleFor(x => x.Shipping!.CustomerNote).Must(CatalogInput.WithoutNul).MaximumLength(500));
            });
        });
        When(x => x.HasDiscountVnd, () =>
            RuleFor(x => x.DiscountVnd).NotNull().Must(v => v is >= 0));
        When(x => x.HasDiscountNote, () =>
            RuleFor(x => x.DiscountNote).Must(CatalogInput.WithoutNul).MaximumLength(500));
        When(x => x.HasShippingFeeVnd && x.ShippingFeeVnd is not null, () =>
            RuleFor(x => x.ShippingFeeVnd!.Value).GreaterThanOrEqualTo(0));
        When(x => x.HasInternalNote, () =>
            RuleFor(x => x.InternalNote).Must(CatalogInput.WithoutNul).MaximumLength(2000));
    }
}

public sealed class ConfirmAdminOrderValidator : AbstractValidator<ConfirmAdminOrder>
{
    public ConfirmAdminOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.CallNote).Must(CatalogInput.WithoutNul).MaximumLength(500);
        RuleFor(x => x.Note).Must(CatalogInput.WithoutNul).MaximumLength(500);
    }
}

public sealed class CancelAdminOrderValidator : AbstractValidator<CancelAdminOrder>
{
    public CancelAdminOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Reason)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => v is not null && v.Trim().Length is >= 1 and <= 500);
    }
}

public sealed class ReorderAdminOrderValidator : AbstractValidator<ReorderAdminOrder>
{
    public ReorderAdminOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.SalesChannel).Must(c => c is null || SalesChannel.IsValidForAdmin(c)).WithMessage("invalid_sales_channel");
        RuleFor(x => x.PaymentMethod).Must(m => m is null || OrderPaymentMethod.IsValid(m)).WithMessage("invalid_payment_method");
        RuleFor(x => x.InternalNote).Must(CatalogInput.WithoutNul).MaximumLength(2000);
    }
}

public sealed class GetAdminOrderPackingSlipValidator : AbstractValidator<GetAdminOrderPackingSlip>
{
    public GetAdminOrderPackingSlipValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

public sealed class ListAdminOrdersHandler(IAdminOrderQueries queries)
    : IQueryHandler<ListAdminOrders, CatalogPage<AdminOrderListItemView>>
{
    public async Task<Result<CatalogPage<AdminOrderListItemView>>> Handle(ListAdminOrders query, CancellationToken cancellationToken)
    {
        if (!AdminOrderFilterParser.TryParseFromFilter(query.From, out var fromUtc))
            return Result.Failure<CatalogPage<AdminOrderListItemView>>(new("validation_failed", "Ngày bắt đầu không hợp lệ.", ErrorType.Validation));
        if (!AdminOrderFilterParser.TryParseToFilter(query.To, out var toUtc, out var toIsExclusive))
            return Result.Failure<CatalogPage<AdminOrderListItemView>>(new("validation_failed", "Ngày kết thúc không hợp lệ.", ErrorType.Validation));

        var q = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        var phoneDigits = CustomerRules.ExtractPhoneSearchDigits(q);

        var filter = new AdminOrderListFilter(
            query.Page,
            query.PageSize,
            query.Sort ?? AdminOrderSortOptions.PlacedAtDesc,
            query.Status,
            query.PaymentStatus,
            query.Channel,
            query.Origin,
            fromUtc,
            toUtc,
            toIsExclusive,
            q,
            phoneDigits,
            query.CustomerId,
            query.Flag);

        return Result.Success(await queries.ListAsync(filter, cancellationToken));
    }
}

public sealed class GetAdminOrderDetailHandler(IAdminOrderQueries queries)
    : IQueryHandler<GetAdminOrderDetail, AdminOrderDetailView>
{
    public async Task<Result<AdminOrderDetailView>> Handle(GetAdminOrderDetail query, CancellationToken cancellationToken)
    {
        var detail = await queries.GetDetailAsync(query.OrderId, query.Actor.HasCostsRead, null, cancellationToken);
        return detail is null
            ? Result.Failure<AdminOrderDetailView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound))
            : Result.Success(detail);
    }
}

public sealed class GetAdminOrderPackingSlipHandler(IAdminOrderQueries queries)
    : IQueryHandler<GetAdminOrderPackingSlip, string>
{
    public async Task<Result<string>> Handle(GetAdminOrderPackingSlip query, CancellationToken cancellationToken)
    {
        var doc = await queries.GetPackingSlipAsync(query.OrderId, cancellationToken);
        if (doc is null)
            return Result.Failure<string>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

        return Result.Success(PackingSlipHtmlRenderer.Render(doc, query.HidePrices));
    }
}

public sealed class AdminOrderCommandHandlers(
    IOrderRepository orders,
    IAdminOrderQueries queries,
    ICustomerOrderResolver customerResolver,
    IAdministrativeReferenceData adminRef,
    IInventoryReservationService reservations,
    IVoucherRedemptionPort vouchers,
    IOrderPricingRead pricingRead,
    IOrderPaymentLedgerReader paymentLedger,
    IOutboxWriter outbox,
    IClock clock) :
    ICommandHandler<CreateAdminOrderHttp, AdminOrderCreatedView>,
    ICommandHandler<PatchAdminOrder, AdminOrderDetailView>,
    ICommandHandler<ConfirmAdminOrder, AdminOrderDetailView>,
    ICommandHandler<CancelAdminOrder, AdminOrderDetailView>,
    ICommandHandler<ReorderAdminOrder, AdminOrderDetailView>
{
    public async Task<Result<AdminOrderCreatedView>> Handle(CreateAdminOrderHttp command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;

        // Defense-in-depth dynamic permission checks BEFORE touching stock or cost data.
        if (command.IsBackfilled && !command.Actor.IsOwner)
            return Result.Failure<AdminOrderCreatedView>(new("forbidden", "Chỉ chủ cửa hàng mới được tạo đơn backfill lịch sử.", ErrorType.Forbidden));
        if (command.Lines.Any(l => l.UnitPriceVnd is not null) && !command.Actor.HasOverridePrice)
            return Result.Failure<AdminOrderCreatedView>(new("forbidden", "Bạn không có quyền sửa đơn giá dòng hàng.", ErrorType.Forbidden));

        var now = clock.UtcNow;
        if (command.IsBackfilled && command.DeliveredAt!.Value > now)
            return Result.Failure<AdminOrderCreatedView>(Error.BusinessRule("invalid_delivered_at", "Thời điểm giao hàng backfill không được ở tương lai."));

        if (!CustomerRules.TryNormalizePhone(command.Shipping.ReceiverPhone, out var normalizedReceiverPhone) || normalizedReceiverPhone is null)
        {
            return Result.Failure<AdminOrderCreatedView>(new(
                "invalid_phone",
                "Số điện thoại người nhận không hợp lệ.",
                ErrorType.Validation,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.receiverPhone"] = ["invalid_phone"] }));
        }

        var provinceCode = command.Shipping.ProvinceCode.Trim();
        var wardCode = NormalizeOptional(command.Shipping.WardCode);
        var wardNameText = NormalizeOptional(command.Shipping.WardNameText);
        if (wardCode is null && wardNameText is null)
        {
            return Result.Failure<AdminOrderCreatedView>(new(
                "validation_failed",
                "Phải cung cấp mã phường/xã hoặc tên phường/xã.",
                ErrorType.Validation,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.wardCode"] = ["ward_required"] }));
        }

        if (!await adminRef.IsReadyAsync(ct))
            return Result.Failure<AdminOrderCreatedView>(Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng."));

        if (!await adminRef.IsActiveSelectionAsync(provinceCode, wardCode, ct))
        {
            return Result.Failure<AdminOrderCreatedView>(new(
                "invalid_address_location",
                "Mã tỉnh/thành hoặc phường/xã không hợp lệ hoặc đã ngừng hoạt động.",
                ErrorType.Validation,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.provinceCode"] = ["invalid_address_location"] }));
        }

        var resolvedLocations = await adminRef.ResolveLocationsAsync([(provinceCode, wardCode)], ct);
        resolvedLocations.TryGetValue((provinceCode, wardCode), out var location);
        var provinceName = location?.ProvinceName ?? "";
        var wardName = wardCode is not null ? (location?.WardName ?? "") : wardNameText!;

        // Resolve existing customer or quick-create new customer via CRM boundary port.
        var customerResult = command.CustomerId is { } existingCustomerId
            ? await customerResolver.ResolveExistingAsync(existingCustomerId, ct)
            : await customerResolver.CreateQuickAdminCustomerAsync(
                command.NewCustomer!.FullName,
                command.NewCustomer.Phone,
                command.Actor.Id,
                ct);
        if (!customerResult.IsSuccess)
            return Result.Failure<AdminOrderCreatedView>(customerResult.Error);

        var customer = customerResult.Value;

        // Lock variants -> stock_levels in global lock order (L2 -> L3 -> L4).
        var variantIds = command.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var locked = await reservations.LockVariantsForOrderAsync(variantIds, now, ct);

        var calcLines = BuildCheckoutLines(command.Lines, locked);
        if (calcLines is null || calcLines.Any(l => !l.ProductActive || !l.VariantActive
            || l.BasePriceVnd is < CheckoutCalculator.MinSellablePriceVnd or > CheckoutCalculator.MaxSellablePriceVnd or null))
            return Result.Failure<AdminOrderCreatedView>(Error.BusinessRule("variant_not_sellable", "Biến thể không tồn tại hoặc không bán được."));

        // BR-PRC-07: check override price floor against avg_cost_vnd without ever leaking avg_cost_vnd.
        if (CheckPriceFloorViolation(command.Lines, locked, command.Actor.HasCostsRead) is { } floorError)
            return Result.Failure<AdminOrderCreatedView>(floorError);

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        AdminManualDiscountInput? manualDiscount = command.DiscountVnd is { } manualAmount
            ? new(manualAmount, command.DiscountNote, command.Actor.HasCostsRead)
            : null;

        var calc = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder,
            now,
            calcLines,
            new CheckoutDiscountInput(Manual: manualDiscount),
            new ShippingRuleInput(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd, command.ShippingFeeVnd),
            new CheckoutLimits(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent)));

        if (!calc.IsSuccess)
            return Result.Failure<AdminOrderCreatedView>(MapCalculationError(calc));

        // R-P206-06: legacy Zalo-only customers have phone_e164 == null; fall back to normalized receiverPhone for contact_phone.
        var contactPhone = customer.PhoneE164 ?? normalizedReceiverPhone;
        var contact = new OrderContactSnapshot(
            customer.FullName,
            contactPhone,
            customer.Email,
            command.Shipping.ReceiverName.Trim(),
            normalizedReceiverPhone,
            provinceCode,
            provinceName,
            wardCode,
            wardName,
            command.Shipping.AddressLine.Trim());

        var isFirstPurchase = customer.IsNewlyCreated
            || (!customer.HadLegacyPurchase
                && customer.DeliveredOrdersCount == 0
                && !await orders.HasAnyDeliveredOrderAsync(customer.CustomerId, ct));
        var acquisitionSource = OrderAcquisitionSource.IsValid(customer.AcquisitionSource)
            ? customer.AcquisitionSource
            : OrderAcquisitionSource.Unknown;
        var attribution = new OrderAttributionSnapshot(acquisitionSource, isFirstPurchase);

        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        var placeholderCode = OrderCode.Format(vietnamDate, "2222");
        var orderId = Guid.CreateVersion7();
        var orderLines = calc.PricedLines.Select(ToOrderLineItem).ToArray();

        Order order;
        if (command.IsBackfilled)
        {
            // BR-ORD-09 / ASSUMPTION-P206-05 = A: delivered immediately, 0 reservations, 0 movements, 0 payments,
            // 0 IProductSoldOutSync, 0 OrderPlaced, emits ONLY OrderDelivered(ReasonCode="backfilled").
            order = Order.CreateBackfilled(
                orderId,
                placeholderCode,
                command.SalesChannel,
                customer.CustomerId,
                command.Actor.Id,
                command.PaymentMethod,
                contact,
                orderLines,
                calc.SubtotalVnd,
                calc.DiscountVnd,
                calc.ShippingFeeVnd,
                calc.TotalVnd,
                calc.DiscountVnd > 0 ? command.DiscountNote : null,
                attribution,
                command.Shipping.CustomerNote,
                command.InternalNote,
                command.DeliveredAt!.Value,
                now);
        }
        else
        {
            order = Order.CreateAdmin(
                orderId,
                placeholderCode,
                command.SalesChannel,
                customer.CustomerId,
                command.Actor.Id,
                command.PaymentMethod,
                contact,
                orderLines,
                calc.SubtotalVnd,
                calc.DiscountVnd,
                calc.ShippingFeeVnd,
                calc.TotalVnd,
                null,
                calc.DiscountVnd > 0 ? command.DiscountNote : null,
                attribution,
                command.Shipping.CustomerNote,
                command.InternalNote,
                now,
                command.ConfirmNow,
                settings.CodRequiresCall,
                command.CallConfirmed,
                command.CallNote);
        }

        var assignedCode = await orders.InsertAsync(order, vietnamDate, ct);
        if (assignedCode is null)
            return Result.Failure<AdminOrderCreatedView>(new("order_code_exhausted", "Không thể cấp mã đơn hàng duy nhất.", ErrorType.Conflict));

        if (!command.IsBackfilled)
        {
            var reservationLines = order.Lines
                .Select(l => new OrderReservationLine(l.Id, l.VariantId, l.ProductId, l.Sku, l.Quantity))
                .ToArray();
            var reserveResult = await reservations.ReserveForOrderAsync(reservationLines, ct);
            if (!reserveResult.IsSuccess)
                return Result.Failure<AdminOrderCreatedView>(MapReservationError(reserveResult));
        }

        EnqueueEvents(order);
        return Result.Success(AdminOrderCreatedView.From(order, customer.CustomerCode));
    }

    public async Task<Result<AdminOrderDetailView>> Handle(PatchAdminOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;

        // Defense-in-depth dynamic permission check BEFORE touching stock or cost data.
        if (command.HasLines && command.Lines!.Any(l => l.UnitPriceVnd is not null) && !command.Actor.HasOverridePrice)
            return Result.Failure<AdminOrderDetailView>(new("forbidden", "Bạn không có quyền sửa đơn giá dòng hàng.", ErrorType.Forbidden));

        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        if (order.IsBackfilled || !OrderStatus.AllowsLineMutation(order.Status))
            return Result.Failure<AdminOrderDetailView>(new("invalid_status_transition", "Chỉ được sửa đơn hàng khi đơn đang chờ hoặc đã xác nhận.", ErrorType.Conflict));

        // Resolve shipping & notes updates if provided.
        var nextReceiverName = order.Contact.ReceiverName;
        var nextReceiverPhone = order.Contact.ReceiverPhone;
        var nextProvinceCode = order.Contact.ProvinceCode;
        var nextProvinceName = order.Contact.ProvinceName;
        var nextWardCode = order.Contact.WardCode;
        var nextWardName = order.Contact.WardName;
        var nextAddressLine = order.Contact.AddressLine;
        var nextCustomerNote = order.CustomerNote;
        var nextInternalNote = command.HasInternalNote ? NormalizeOptional(command.InternalNote) : order.InternalNote;

        if (command.HasShipping && command.Shipping is { } ship)
        {
            if (ship.HasReceiverName) nextReceiverName = ship.ReceiverName!.Trim();
            if (ship.HasReceiverPhone)
            {
                if (!CustomerRules.TryNormalizePhone(ship.ReceiverPhone, out var normalizedPhone) || normalizedPhone is null)
                {
                    return Result.Failure<AdminOrderDetailView>(new(
                        "invalid_phone",
                        "Số điện thoại người nhận không hợp lệ.",
                        ErrorType.Validation,
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.receiverPhone"] = ["invalid_phone"] }));
                }
                nextReceiverPhone = normalizedPhone;
            }
            if (ship.HasProvinceCode) nextProvinceCode = ship.ProvinceCode!.Trim();
            if (ship.HasWardCode) nextWardCode = NormalizeOptional(ship.WardCode);
            if (ship.HasAddressLine) nextAddressLine = ship.AddressLine!.Trim();
            if (ship.HasCustomerNote) nextCustomerNote = NormalizeOptional(ship.CustomerNote);

            var locationChanged = !string.Equals(nextProvinceCode, order.Contact.ProvinceCode, StringComparison.Ordinal)
                || !string.Equals(nextWardCode, order.Contact.WardCode, StringComparison.Ordinal);

            if (locationChanged)
            {
                var providedWardText = ship.HasWardNameText ? NormalizeOptional(ship.WardNameText) : null;
                if (nextWardCode is null)
                {
                    var effectiveWardText = ship.HasWardNameText
                        ? providedWardText
                        : (order.Contact.WardCode is null ? order.Contact.WardName : null);
                    if (string.IsNullOrWhiteSpace(effectiveWardText))
                    {
                        return Result.Failure<AdminOrderDetailView>(new(
                            "validation_failed",
                            "Phải cung cấp mã phường/xã hoặc tên phường/xã.",
                            ErrorType.Validation,
                            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.wardCode"] = ["ward_required"] }));
                    }
                    nextWardName = effectiveWardText;
                }

                if (!await adminRef.IsReadyAsync(ct))
                    return Result.Failure<AdminOrderDetailView>(Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng."));

                if (!await adminRef.IsActiveSelectionAsync(nextProvinceCode, nextWardCode, ct))
                {
                    return Result.Failure<AdminOrderDetailView>(new(
                        "invalid_address_location",
                        "Mã tỉnh/thành hoặc phường/xã không hợp lệ hoặc đã ngừng hoạt động.",
                        ErrorType.Validation,
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.provinceCode"] = ["invalid_address_location"] }));
                }

                var resolvedLocations = await adminRef.ResolveLocationsAsync([(nextProvinceCode, nextWardCode)], ct);
                resolvedLocations.TryGetValue((nextProvinceCode, nextWardCode), out var loc);
                nextProvinceName = loc?.ProvinceName ?? "";
                if (nextWardCode is not null)
                    nextWardName = loc?.WardName ?? "";
            }
            else if (ship.HasWardNameText && nextWardCode is null)
            {
                var providedWardText = NormalizeOptional(ship.WardNameText);
                if (providedWardText is null)
                {
                    return Result.Failure<AdminOrderDetailView>(new(
                        "validation_failed",
                        "Tên phường/xã không được để trống khi không có mã phường/xã.",
                        ErrorType.Validation,
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["shipping.wardNameText"] = ["ward_required"] }));
                }
                nextWardName = providedWardText;
            }
        }

        if (command.HasShipping || command.HasInternalNote)
        {
            order.UpdateAdminDetails(
                nextReceiverName,
                nextReceiverPhone,
                nextProvinceCode,
                nextProvinceName,
                nextWardCode,
                nextWardName,
                nextAddressLine,
                nextCustomerNote,
                nextInternalNote,
                now);
        }

        var financialsTouched = command.HasLines || command.HasDiscountVnd || command.HasDiscountNote || command.HasShippingFeeVnd;
        if (!financialsTouched)
        {
            // BR-PRC-06 / ASSUMPTION-P206-01: sales may edit shipping/internalNote on an owner-discounted order without touching lines/discount.
            await orders.SaveUpdatedOrderAsync(order, null, ct);
            return await ReloadDetailAsync(order.Id, command.Actor.HasCostsRead, null, ct);
        }

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        var warnings = new List<AdminOrderWarningView>();

        if (command.HasLines)
        {
            var requestedLines = command.Lines!;
            var allVariantIds = order.Lines.Select(x => x.VariantId)
                .Concat(requestedLines.Select(x => x.VariantId))
                .Distinct()
                .ToArray();

            // L2 -> L3 -> L4: lock products -> variants -> stock_levels before L5 (pricing.vouchers).
            var locked = await reservations.LockVariantsForOrderAsync(allVariantIds, now, ct);

            var calcLines = BuildCheckoutLines(requestedLines, locked);
            if (calcLines is null || calcLines.Any(l => !l.ProductActive || !l.VariantActive
                || l.BasePriceVnd is < CheckoutCalculator.MinSellablePriceVnd or > CheckoutCalculator.MaxSellablePriceVnd or null))
                return Result.Failure<AdminOrderDetailView>(Error.BusinessRule("variant_not_sellable", "Biến thể không tồn tại hoặc không bán được."));

            // BR-PRC-07: verify price floor without leaking avg_cost_vnd.
            if (CheckPriceFloorViolation(requestedLines, locked, command.Actor.HasCostsRead) is { } floorError)
                return Result.Failure<AdminOrderDetailView>(floorError);

            // L5: lock existing applied voucher if present.
            var existingRedemption = await vouchers.GetByOrderIdAsync(order.Id, ct);
            var existingAppliedVoucherId = existingRedemption is { Status: "applied" } ? existingRedemption.VoucherId : (Guid?)null;
            VoucherSnapshot? lockedExistingVoucher = null;
            if (existingAppliedVoucherId is { } activeVoucherId)
            {
                var lockedVouchers = await vouchers.LockByIdsAsync([activeVoucherId], ct);
                lockedVouchers.TryGetValue(activeVoucherId, out lockedExistingVoucher);
            }

            CheckoutDiscountInput discountInput;
            string? effectiveDiscountNote = null;
            var retainingExistingVoucher = false;

            if (command.HasDiscountVnd)
            {
                var requestedDiscount = command.DiscountVnd!.Value;
                if (requestedDiscount > 0)
                {
                    effectiveDiscountNote = command.HasDiscountNote
                        ? NormalizeOptional(command.DiscountNote)
                        : NormalizeOptional(order.DiscountNote);
                    if (effectiveDiscountNote is null)
                    {
                        return Result.Failure<AdminOrderDetailView>(new(
                            "validation_failed",
                            "Giảm giá tay bắt buộc có ghi chú.",
                            ErrorType.Validation,
                            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["discountNote"] = ["discount_note_required"] }));
                    }
                    discountInput = new CheckoutDiscountInput(
                        Manual: new AdminManualDiscountInput(requestedDiscount, effectiveDiscountNote, command.Actor.HasCostsRead));
                }
                else
                {
                    effectiveDiscountNote = null;
                    discountInput = CheckoutDiscountInput.None;
                }
            }
            else if (existingAppliedVoucherId is { } retainedVoucherId && lockedExistingVoucher is not null)
            {
                retainingExistingVoucher = true;
                var snapshotForCalc = lockedExistingVoucher with { UsedCount = Math.Max(0, lockedExistingVoucher.UsedCount - 1) };
                var appliedByCustomer = await vouchers.CountAppliedByCustomerAsync(
                    retainedVoucherId,
                    order.CustomerId,
                    excludeOrderId: order.Id,
                    ct);
                discountInput = new CheckoutDiscountInput(
                    Voucher: new VoucherDiscountInput(lockedExistingVoucher.Code, snapshotForCalc, order.CustomerId, appliedByCustomer));
            }
            else if (order.DiscountVnd > 0)
            {
                effectiveDiscountNote = command.HasDiscountNote
                    ? NormalizeOptional(command.DiscountNote)
                    : NormalizeOptional(order.DiscountNote);
                if (effectiveDiscountNote is null)
                {
                    return Result.Failure<AdminOrderDetailView>(new(
                        "validation_failed",
                        "Giảm giá tay bắt buộc có ghi chú.",
                        ErrorType.Validation,
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["discountNote"] = ["discount_note_required"] }));
                }
                discountInput = new CheckoutDiscountInput(
                    Manual: new AdminManualDiscountInput(order.DiscountVnd, effectiveDiscountNote, command.Actor.HasCostsRead));
            }
            else
            {
                effectiveDiscountNote = null;
                discountInput = CheckoutDiscountInput.None;
            }

            // ASSUMPTION-P206-01 = A (3-way shippingFeeVnd):
            // - omitted: admin preserves existing shippingFeeVnd; web recalculates because lines changed
            // - explicit null: recalculate per BR-SHP-01
            // - explicit value: apply value
            long? overrideShippingFee = command.HasShippingFeeVnd
                ? command.ShippingFeeVnd
                : (order.Origin == OrderOrigin.Admin ? order.ShippingFeeVnd : (long?)null);

            var shippingRule = new ShippingRuleInput(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd, overrideShippingFee);
            var limits = new CheckoutLimits(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent);

            var calc = CheckoutCalculator.Calculate(new(
                CheckoutMode.AdminOrder,
                now,
                calcLines,
                discountInput,
                shippingRule,
                limits));

            // BR-ORD-08 / G12 = A: auto-remove applied voucher if subtotal drops below min_order_vnd.
            if (!calc.IsSuccess && retainingExistingVoucher && calc.ErrorCode == "voucher_min_order")
            {
                calc = CheckoutCalculator.Calculate(new(
                    CheckoutMode.AdminOrder,
                    now,
                    calcLines,
                    CheckoutDiscountInput.None,
                    shippingRule,
                    limits));
                warnings.Add(new AdminOrderWarningView(
                    "voucher_removed_min_order",
                    "Voucher đã được tự động gỡ vì giá trị đơn hàng dưới mức tối thiểu."));
            }

            if (!calc.IsSuccess)
                return Result.Failure<AdminOrderDetailView>(MapCalculationError(calc));

            var nextItems = calc.PricedLines.Select(ToOrderLineItem).ToArray();
            var finalDiscountNote = calc.AppliedVoucherId is null && calc.DiscountVnd > 0 ? effectiveDiscountNote : null;
            var delta = order.ReplaceLines(
                nextItems,
                calc.SubtotalVnd,
                calc.DiscountVnd,
                calc.ShippingFeeVnd,
                calc.TotalVnd,
                calc.AppliedVoucherId,
                finalDiscountNote,
                now);

            await RecalculateOrderPaymentStatusAsync(order, now, ct);

            var syncResult = await reservations.SyncOrderLinesAsync(
                order.Id,
                delta,
                innerCt => orders.SaveUpdatedOrderAsync(order, delta, innerCt),
                ct);
            if (!syncResult.IsSuccess)
                return Result.Failure<AdminOrderDetailView>(MapReservationError(syncResult));

            // Synchronize voucher redemption in the same transaction (G12 = A).
            if (calc.AppliedVoucherId is null)
            {
                if (existingAppliedVoucherId is not null)
                    await vouchers.RevertForOrderAsync(order.Id, ct);
            }
            else if (existingAppliedVoucherId == calc.AppliedVoucherId)
            {
                await vouchers.UpdateAppliedAmountAsync(order.Id, calc.DiscountVnd, ct);
            }
        }
        else
        {
            // Lines not modified: preserve snapshotted line prices and adjust discount/shippingFee only.
            var existingRedemption = await vouchers.GetByOrderIdAsync(order.Id, ct);
            var existingAppliedVoucherId = existingRedemption is { Status: "applied" } ? existingRedemption.VoucherId : (Guid?)null;

            long nextDiscountVnd;
            string? nextDiscountNote;
            Guid? nextVoucherId;
            var revertExistingVoucher = false;

            if (command.HasDiscountVnd)
            {
                nextDiscountVnd = command.DiscountVnd!.Value;
                if (nextDiscountVnd > order.SubtotalVnd)
                    return Result.Failure<AdminOrderDetailView>(new("validation_failed", "Giảm giá tay phải nằm trong khoảng 0 đến tiền hàng.", ErrorType.Validation));

                if (nextDiscountVnd > 0)
                {
                    nextDiscountNote = command.HasDiscountNote
                        ? NormalizeOptional(command.DiscountNote)
                        : NormalizeOptional(order.DiscountNote);
                    if (nextDiscountNote is null)
                    {
                        return Result.Failure<AdminOrderDetailView>(new(
                            "validation_failed",
                            "Giảm giá tay bắt buộc có ghi chú.",
                            ErrorType.Validation,
                            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["discountNote"] = ["discount_note_required"] }));
                    }
                    if (!command.Actor.HasCostsRead
                        && (Int128)nextDiscountVnd * 100 > (Int128)order.SubtotalVnd * settings.MaxStaffDiscountPercent)
                    {
                        return Result.Failure<AdminOrderDetailView>(Error.BusinessRule("discount_requires_owner", "Mức giảm giá vượt giới hạn nhân viên."));
                    }
                }
                else
                {
                    nextDiscountNote = null;
                }

                nextVoucherId = null;
                revertExistingVoucher = existingAppliedVoucherId is not null;
            }
            else
            {
                nextDiscountVnd = order.DiscountVnd;
                nextVoucherId = order.VoucherId;
                if (command.HasDiscountNote)
                {
                    if (nextDiscountVnd > 0 && nextVoucherId is null)
                    {
                        nextDiscountNote = NormalizeOptional(command.DiscountNote);
                        if (nextDiscountNote is null)
                        {
                            return Result.Failure<AdminOrderDetailView>(new(
                                "validation_failed",
                                "Giảm giá tay bắt buộc có ghi chú.",
                                ErrorType.Validation,
                                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["discountNote"] = ["discount_note_required"] }));
                        }
                    }
                    else
                    {
                        nextDiscountNote = nextDiscountVnd == 0 ? null : order.DiscountNote;
                    }
                }
                else
                {
                    nextDiscountNote = order.DiscountNote;
                }
            }

            // ASSUMPTION-P206-01 = A (3-way shippingFeeVnd):
            long nextShippingFeeVnd;
            if (command.HasShippingFeeVnd)
            {
                nextShippingFeeVnd = command.ShippingFeeVnd ?? ComputeShippingFeeVnd(order.SubtotalVnd, nextDiscountVnd, settings);
            }
            else if (order.Origin == OrderOrigin.Web && command.HasDiscountVnd)
            {
                nextShippingFeeVnd = ComputeShippingFeeVnd(order.SubtotalVnd, nextDiscountVnd, settings);
            }
            else
            {
                nextShippingFeeVnd = order.ShippingFeeVnd;
            }

            order.UpdateAdminAdjustments(nextDiscountVnd, nextShippingFeeVnd, nextVoucherId, nextDiscountNote, now);
            await RecalculateOrderPaymentStatusAsync(order, now, ct);
            await orders.SaveUpdatedOrderAsync(order, null, ct);

            if (revertExistingVoucher)
                await vouchers.RevertForOrderAsync(order.Id, ct);
        }

        return await ReloadDetailAsync(order.Id, command.Actor.HasCostsRead, warnings, ct);
    }

    public async Task<Result<AdminOrderDetailView>> Handle(ConfirmAdminOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        if (!order.IsBackfilled)
            await RecalculateOrderPaymentStatusAsync(order, now, ct);

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        var effectiveNote = NormalizeOptional(command.CallNote) ?? NormalizeOptional(command.Note);
        order.Confirm(OrderActorType.Staff, command.Actor.Id, now, settings.CodRequiresCall, command.CallConfirmed, effectiveNote);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return await ReloadDetailAsync(order.Id, command.Actor.HasCostsRead, null, ct);
    }

    public async Task<Result<AdminOrderDetailView>> Handle(CancelAdminOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, command.ExpectedVersion, ct);
        if (loaded.ConcurrencyConflict) return ConcurrencyFailure();
        if (loaded.Order is not { } order) return NotFoundFailure();

        var hasActiveShipment = order.Status == OrderStatus.Confirmed
            && await orders.HasActiveOrderShipmentAsync(order.Id, ct);
        order.Cancel(OrderActorType.Staff, command.Actor.Id, command.Reason, now, hasActiveShipment);

        await RecalculateOrderPaymentStatusAsync(order, now, ct);
        await reservations.ReleaseForOrderAsync(order.Id, ReservationCloseReason.OrderCancelled, ct);
        await vouchers.RevertForOrderAsync(order.Id, ct);

        await orders.SaveUpdatedOrderAsync(order, null, ct);
        EnqueueEvents(order);
        return await ReloadDetailAsync(order.Id, command.Actor.HasCostsRead, null, ct);
    }

    public async Task<Result<AdminOrderDetailView>> Handle(ReorderAdminOrder command, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = clock.UtcNow;

        // Lock customer FOR SHARE (L0) BEFORE locking source order (L1) so lock order strictly matches
        // Erase/Merge (crm.customers -> sales.orders) with zero AB-BA deadlock risk.
        var sourceCustomerId = await orders.GetCustomerIdAsync(command.OrderId, ct);
        if (sourceCustomerId is null) return NotFoundFailure();

        var customerResult = await customerResolver.ResolveExistingAsync(sourceCustomerId.Value, ct);
        if (!customerResult.IsSuccess)
            return Result.Failure<AdminOrderDetailView>(customerResult.Error);
        var customer = customerResult.Value;

        // Lock source order (L1) to read a consistent snapshot of its status, lines, customer, and address.
        var loaded = await orders.LoadForUpdateAsync(command.OrderId, expectedVersion: null, ct);
        if (loaded.Order is not { } source) return NotFoundFailure();

        // ASSUMPTION-P206-03 = A: allow reorder from all 3 terminal states; reject pending/confirmed/shipping with 409.
        if (!OrderStatus.IsTerminal(source.Status))
            return Result.Failure<AdminOrderDetailView>(new("invalid_status_transition", "Chỉ được tạo lại đơn từ đơn ở trạng thái cuối.", ErrorType.Conflict));

        if (!await adminRef.IsReadyAsync(ct))
            return Result.Failure<AdminOrderDetailView>(Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng."));

        if (!await adminRef.IsActiveSelectionAsync(source.Contact.ProvinceCode, source.Contact.WardCode, ct))
        {
            return Result.Failure<AdminOrderDetailView>(new(
                "invalid_address_location",
                "Địa chỉ giao hàng của đơn gốc không còn hoạt động trong danh mục địa giới.",
                ErrorType.Validation));
        }

        var resolvedLocations = await adminRef.ResolveLocationsAsync([(source.Contact.ProvinceCode, source.Contact.WardCode)], ct);
        resolvedLocations.TryGetValue((source.Contact.ProvinceCode, source.Contact.WardCode), out var location);
        var provinceName = location?.ProvinceName ?? source.Contact.ProvinceName;
        var wardName = source.Contact.WardCode is not null ? (location?.WardName ?? source.Contact.WardName) : source.Contact.WardName;

        var targetSalesChannel = command.SalesChannel
            ?? (source.SalesChannel == SalesChannel.Web ? SalesChannel.Other : source.SalesChannel);
        var targetPaymentMethod = command.PaymentMethod ?? source.PaymentMethod;

        // CRITICAL (ASSUMPTION-P206-03 = A): ALWAYS re-price against current catalog prices and current stock;
        // NEVER copy old price overrides, old vouchers/discounts, old payments/refunds, or old shipments.
        var freshLineRequests = source.Lines
            .Select(l => new AdminOrderLineInput(l.VariantId, l.Quantity, UnitPriceVnd: null, PriceOverrideReason: null))
            .ToArray();

        var variantIds = freshLineRequests.Select(x => x.VariantId).Distinct().ToArray();
        var locked = await reservations.LockVariantsForOrderAsync(variantIds, now, ct);

        var calcLines = BuildCheckoutLines(freshLineRequests, locked);
        if (calcLines is null)
            return Result.Failure<AdminOrderDetailView>(Error.BusinessRule("variant_not_sellable", "Một số biến thể của đơn gốc không còn mở bán."));

        var settings = await pricingRead.GetOrderSettingsAsync(ct);
        var calc = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder,
            now,
            calcLines,
            CheckoutDiscountInput.None,
            new ShippingRuleInput(settings.ShippingFlatFeeVnd, settings.ShippingFreeThresholdVnd, AdminOverrideShippingFeeVnd: null),
            new CheckoutLimits(settings.MaxQuantityPerLineWeb, settings.MaxStaffDiscountPercent)));

        if (!calc.IsSuccess)
            return Result.Failure<AdminOrderDetailView>(MapCalculationError(calc));

        var contactPhone = customer.PhoneE164 ?? source.Contact.ReceiverPhone;
        var contact = new OrderContactSnapshot(
            customer.FullName,
            contactPhone,
            customer.Email,
            source.Contact.ReceiverName,
            source.Contact.ReceiverPhone,
            source.Contact.ProvinceCode,
            provinceName,
            source.Contact.WardCode,
            wardName,
            source.Contact.AddressLine);

        var isFirstPurchase = !customer.HadLegacyPurchase
            && customer.DeliveredOrdersCount == 0
            && !await orders.HasAnyDeliveredOrderAsync(customer.CustomerId, ct);
        var acquisitionSource = OrderAcquisitionSource.IsValid(customer.AcquisitionSource)
            ? customer.AcquisitionSource
            : OrderAcquisitionSource.Unknown;
        var attribution = new OrderAttributionSnapshot(acquisitionSource, isFirstPurchase);

        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        var placeholderCode = OrderCode.Format(vietnamDate, "2222");
        var newOrderId = Guid.CreateVersion7();
        var orderLines = calc.PricedLines.Select(ToOrderLineItem).ToArray();

        var newOrder = Order.CreateAdmin(
            newOrderId,
            placeholderCode,
            targetSalesChannel,
            customer.CustomerId,
            command.Actor.Id,
            targetPaymentMethod,
            contact,
            orderLines,
            calc.SubtotalVnd,
            calc.DiscountVnd,
            calc.ShippingFeeVnd,
            calc.TotalVnd,
            null,
            null,
            attribution,
            source.CustomerNote,
            NormalizeOptional(command.InternalNote),
            now,
            confirmNow: false);

        var assignedCode = await orders.InsertAsync(newOrder, vietnamDate, ct);
        if (assignedCode is null)
            return Result.Failure<AdminOrderDetailView>(new("order_code_exhausted", "Không thể cấp mã đơn hàng duy nhất.", ErrorType.Conflict));

        var reservationLines = newOrder.Lines
            .Select(l => new OrderReservationLine(l.Id, l.VariantId, l.ProductId, l.Sku, l.Quantity))
            .ToArray();
        var reserveResult = await reservations.ReserveForOrderAsync(reservationLines, ct);
        if (!reserveResult.IsSuccess)
            return Result.Failure<AdminOrderDetailView>(MapReservationError(reserveResult));

        EnqueueEvents(newOrder);
        return await ReloadDetailAsync(newOrder.Id, command.Actor.HasCostsRead, null, ct);
    }

    private async Task RecalculateOrderPaymentStatusAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(order.Id, ct);
        var calc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);
        order.ApplyPaymentCalculation(calc, now);
    }

    private async Task<Result<AdminOrderDetailView>> ReloadDetailAsync(
        Guid orderId,
        bool includeCosts,
        IReadOnlyList<AdminOrderWarningView>? warnings,
        CancellationToken ct)
    {
        var detail = await queries.GetDetailAsync(orderId, includeCosts, warnings, ct);
        return detail is null
            ? throw new InvalidOperationException("Updated order could not be reloaded.")
            : Result.Success(detail);
    }

    // BR-PRC-07: static message and variantId only — NEVER include avg_cost_vnd, ratio, or floor value.
    private static Error? CheckPriceFloorViolation(
        IReadOnlyList<AdminOrderLineInput> requested,
        IReadOnlyDictionary<Guid, LockedOrderVariant> locked,
        bool actorHasCostsRead)
    {
        if (actorHasCostsRead) return null;

        foreach (var line in requested)
        {
            if (line.UnitPriceVnd is not { } customPrice) continue;
            if (!locked.TryGetValue(line.VariantId, out var variant)) continue;
            if (variant.AvgCostVnd is { } avgCost && customPrice < avgCost)
            {
                return new Error(
                    "price_below_allowed",
                    "Đơn giá sửa tay thấp hơn mức cho phép.",
                    ErrorType.BusinessRule,
                    ValidationErrors: null,
                    Data: new Dictionary<string, object?>(StringComparer.Ordinal) { ["variantId"] = line.VariantId });
            }
        }

        return null;
    }

    private static List<CheckoutLineInput>? BuildCheckoutLines(
        IReadOnlyList<AdminOrderLineInput> requested,
        IReadOnlyDictionary<Guid, LockedOrderVariant> locked)
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
                line.UnitPriceVnd,
                line.PriceOverrideReason));
        }
        return result;
    }

    private static long ComputeShippingFeeVnd(long subtotalVnd, long discountVnd, OrderSettingsSnapshot settings)
    {
        var afterDiscount = checked(subtotalVnd - discountVnd);
        return settings.ShippingFreeThresholdVnd is { } threshold && afterDiscount >= threshold
            ? 0L
            : settings.ShippingFlatFeeVnd;
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

    private static Result<AdminOrderDetailView> NotFoundFailure() =>
        Result.Failure<AdminOrderDetailView>(new("not_found", "Không tìm thấy đơn hàng.", ErrorType.NotFound));

    private static Result<AdminOrderDetailView> ConcurrencyFailure() =>
        Result.Failure<AdminOrderDetailView>(new("concurrency_conflict", "Đơn hàng đã được cập nhật bởi thao tác khác; tải lại trước khi lưu.", ErrorType.Conflict));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
