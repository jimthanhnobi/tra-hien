using TeaPremium.Domain.Pricing;

namespace TeaPremium.Domain.Orders;

public enum CheckoutMode
{
    WebQuote,
    WebPlaceOrder,
    AdminOrder
}

public sealed record CheckoutLineInput(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? VariantName,
    bool ProductActive,
    bool VariantActive,
    long? BasePriceVnd,
    IReadOnlyList<ScheduledPrice> Promotions,
    bool IsUniquePiece,
    int AvailableStock,
    int RequestedQuantity,
    long? OverrideUnitPriceVnd = null,
    string? PriceOverrideReason = null);

public sealed record VoucherSnapshot(
    Guid Id,
    string Code,
    string Kind,
    string DiscountType,
    long DiscountValue,
    long? MaxDiscountVnd,
    long MinOrderVnd,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int? UsageLimit,
    int PerCustomerLimit,
    Guid? BoundCustomerId,
    int UsedCount,
    string Status);

public sealed record VoucherDiscountInput(
    string Code,
    VoucherSnapshot? Voucher,
    Guid? CustomerId,
    int CustomerAppliedCount);

public sealed record AdminManualDiscountInput(
    long DiscountVnd,
    string? DiscountNote,
    bool ActorHasCostsRead);

public sealed record CheckoutDiscountInput(
    VoucherDiscountInput? Voucher = null,
    string? ReferralCode = null,
    AdminManualDiscountInput? Manual = null)
{
    public static CheckoutDiscountInput None { get; } = new();
}

public sealed record ShippingRuleInput(
    long FlatFeeVnd,
    long? FreeThresholdVnd,
    long? AdminOverrideShippingFeeVnd = null);

public sealed record CheckoutLimits(
    int MaxQuantityPerLineWeb = 10,
    int MaxStaffDiscountPercent = 10);

public sealed record CheckoutCalculationInput(
    CheckoutMode Mode,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<CheckoutLineInput> Lines,
    CheckoutDiscountInput Discount,
    ShippingRuleInput Shipping,
    CheckoutLimits? Limits = null);

public sealed record PricedCheckoutLine(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? VariantName,
    long ListPriceVnd,
    long UnitPriceVnd,
    string PriceSource,
    string? PriceOverrideReason,
    int Quantity,
    long LineTotalVnd,
    bool IsAvailable,
    string? UnavailableReason);

public sealed record OutOfStockDetail(Guid VariantId, string Sku, int Available);

public sealed record CheckoutWarning(string Code, Guid? VariantId, string Message);

public sealed record CheckoutCalculationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PricedCheckoutLine> PricedLines,
    IReadOnlyList<PricedCheckoutLine> UnavailableLines,
    long SubtotalVnd,
    long DiscountVnd,
    Guid? AppliedVoucherId,
    string? DiscountLabel,
    long ShippingFeeVnd,
    long TotalVnd,
    IReadOnlyList<CheckoutWarning> Warnings,
    IReadOnlyList<OutOfStockDetail> OutOfStockItems)
{
    public static CheckoutCalculationResult Failure(
        string code,
        string message,
        IReadOnlyList<OutOfStockDetail>? outOfStockItems = null) =>
        new(false, code, message, [], [], 0, 0, null, null, 0, 0, [], outOfStockItems ?? []);
}

// BR-CRT-05, BR-PRC-01/02/04/06/07, BR-SHP-01, BR-CAT-15, GAP-P205-01=A, ASSUMPTION-P205-02=A.
public static class CheckoutCalculator
{
    public const long MinSellablePriceVnd = 1L;
    public const long MaxSellablePriceVnd = 10_000_000_000L;

    public static CheckoutCalculationResult Calculate(CheckoutCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var limits = input.Limits ?? new CheckoutLimits();
        if (limits.MaxQuantityPerLineWeb < 1 || limits.MaxStaffDiscountPercent is < 0 or > 100)
            return CheckoutCalculationResult.Failure("validation_failed", "Cấu hình giới hạn đơn hàng không hợp lệ.");

        if (input.Shipping.FlatFeeVnd < 0 || input.Shipping.FreeThresholdVnd is < 0 || input.Shipping.AdminOverrideShippingFeeVnd is < 0)
            return CheckoutCalculationResult.Failure("validation_failed", "Cấu hình phí vận chuyển không hợp lệ.");
        if (input.Shipping.AdminOverrideShippingFeeVnd is not null && input.Mode != CheckoutMode.AdminOrder)
            return CheckoutCalculationResult.Failure("validation_failed", "Chỉ đơn quản trị mới được sửa phí vận chuyển.");

        var discountMechanisms = (input.Discount.Voucher is not null ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(input.Discount.ReferralCode) ? 1 : 0)
            + (input.Discount.Manual is not null ? 1 : 0);
        if (discountMechanisms > 1)
            return CheckoutCalculationResult.Failure("validation_failed", "Mỗi đơn hàng chỉ được áp dụng tối đa một cơ chế giảm giá.");

        if (input.Lines.Count == 0 && input.Mode != CheckoutMode.WebQuote)
            return CheckoutCalculationResult.Failure("validation_failed", "Đơn hàng phải có ít nhất một dòng sản phẩm.");

        var seenVariants = new HashSet<Guid>();
        var pricedLines = new List<PricedCheckoutLine>(input.Lines.Count);
        var unavailableLines = new List<PricedCheckoutLine>();
        var warnings = new List<CheckoutWarning>();
        var outOfStock = new List<OutOfStockDetail>();
        long subtotalVnd = 0;

        foreach (var line in input.Lines)
        {
            if (line.VariantId == Guid.Empty || line.ProductId == Guid.Empty || !seenVariants.Add(line.VariantId))
                return CheckoutCalculationResult.Failure("validation_failed", "Danh sách biến thể không hợp lệ hoặc bị trùng.");

            if (line.OverrideUnitPriceVnd is not null && input.Mode != CheckoutMode.AdminOrder)
                return CheckoutCalculationResult.Failure("validation_failed", "Đơn web không được sửa giá tay.");

            // ASSUMPTION-P205-02 = A: both web and admin require active product + active variant + valid price.
            var sellable = line.ProductActive
                && line.VariantActive
                && line.BasePriceVnd is >= MinSellablePriceVnd and <= MaxSellablePriceVnd;

            if (!sellable)
            {
                if (input.Mode == CheckoutMode.WebQuote)
                {
                    var unavailable = new PricedCheckoutLine(
                        line.VariantId, line.ProductId, line.Sku, line.ProductName, line.VariantName,
                        0, 0, OrderPriceSource.List, null, 0, 0, false, "variant_not_sellable");
                    pricedLines.Add(unavailable);
                    unavailableLines.Add(unavailable);
                    warnings.Add(new("variant_not_sellable", line.VariantId, "Sản phẩm hiện không mở bán."));
                    continue;
                }
                if (input.Mode == CheckoutMode.WebPlaceOrder)
                    return CheckoutCalculationResult.Failure("cart_has_unavailable_items", "Giỏ hàng có sản phẩm không còn mở bán.");
                return CheckoutCalculationResult.Failure("variant_not_sellable", "Biến thể không đủ điều kiện mở bán.");
            }

            if (line.RequestedQuantity < 1)
                return CheckoutCalculationResult.Failure("validation_failed", "Số lượng sản phẩm phải lớn hơn hoặc bằng 1.");

            var effectivePrice = PricingCalculator.Calculate(true, line.BasePriceVnd, line.Promotions, input.EvaluatedAt)
                ?? throw new InvalidOperationException("Sellable variant produced null effective price.");

            int finalQty;
            long unitPriceVnd;
            string priceSource;
            string? overrideReason = null;

            if (input.Mode == CheckoutMode.WebQuote)
            {
                if (line.AvailableStock <= 0)
                {
                    var unavailable = new PricedCheckoutLine(
                        line.VariantId, line.ProductId, line.Sku, line.ProductName, line.VariantName,
                        effectivePrice.BasePriceVnd, effectivePrice.EffectivePriceVnd,
                        effectivePrice.EffectivePriceVnd < effectivePrice.BasePriceVnd ? OrderPriceSource.Sale : OrderPriceSource.List,
                        null, 0, 0, false, "out_of_stock");
                    pricedLines.Add(unavailable);
                    unavailableLines.Add(unavailable);
                    warnings.Add(new("out_of_stock", line.VariantId, "Sản phẩm đã hết hàng."));
                    continue;
                }

                var maxAllowed = line.IsUniquePiece
                    ? 1
                    : Math.Min(line.AvailableStock, limits.MaxQuantityPerLineWeb);
                if (line.RequestedQuantity > maxAllowed)
                {
                    finalQty = maxAllowed;
                    warnings.Add(new("quantity_adjusted", line.VariantId, "Số lượng đã được điều chỉnh về mức tối đa có thể mua."));
                }
                else
                {
                    finalQty = line.RequestedQuantity;
                }

                unitPriceVnd = effectivePrice.EffectivePriceVnd;
                priceSource = effectivePrice.EffectivePriceVnd < effectivePrice.BasePriceVnd ? OrderPriceSource.Sale : OrderPriceSource.List;
            }
            else if (input.Mode == CheckoutMode.WebPlaceOrder)
            {
                if (line.IsUniquePiece && line.RequestedQuantity > 1)
                    return CheckoutCalculationResult.Failure("unique_piece_violation", "Sản phẩm độc bản chỉ được đặt số lượng 1.");
                if (line.RequestedQuantity > limits.MaxQuantityPerLineWeb)
                    return CheckoutCalculationResult.Failure("validation_failed", "Số lượng vượt quá giới hạn mỗi dòng trên web.");
                if (line.AvailableStock <= 0 || line.RequestedQuantity > line.AvailableStock)
                {
                    outOfStock.Add(new(line.VariantId, line.Sku, Math.Max(0, line.AvailableStock)));
                    continue;
                }

                finalQty = line.RequestedQuantity;
                unitPriceVnd = effectivePrice.EffectivePriceVnd;
                priceSource = effectivePrice.EffectivePriceVnd < effectivePrice.BasePriceVnd ? OrderPriceSource.Sale : OrderPriceSource.List;
            }
            else
            {
                if (line.RequestedQuantity > 1000)
                    return CheckoutCalculationResult.Failure("validation_failed", "Số lượng mỗi dòng không được vượt quá 1000.");
                if (line.IsUniquePiece && line.RequestedQuantity > 1)
                    return CheckoutCalculationResult.Failure("unique_piece_violation", "Sản phẩm độc bản chỉ được đặt số lượng 1.");

                finalQty = line.RequestedQuantity;
                if (line.OverrideUnitPriceVnd is { } customPrice)
                {
                    var trimmed = string.IsNullOrWhiteSpace(line.PriceOverrideReason) ? null : line.PriceOverrideReason.Trim();
                    if (customPrice < 0 || trimmed is null || trimmed.Length < 3)
                        return CheckoutCalculationResult.Failure("validation_failed", "Sửa giá tay cần đơn giá không âm và lý do tối thiểu 3 ký tự.");
                    unitPriceVnd = customPrice;
                    priceSource = OrderPriceSource.Override;
                    overrideReason = trimmed;
                }
                else
                {
                    unitPriceVnd = effectivePrice.EffectivePriceVnd;
                    priceSource = effectivePrice.EffectivePriceVnd < effectivePrice.BasePriceVnd ? OrderPriceSource.Sale : OrderPriceSource.List;
                }
            }

            var lineTotalVnd = checked((long)finalQty * unitPriceVnd);
            subtotalVnd = checked(subtotalVnd + lineTotalVnd);
            pricedLines.Add(new(
                line.VariantId,
                line.ProductId,
                line.Sku,
                line.ProductName,
                line.VariantName,
                effectivePrice.BasePriceVnd,
                unitPriceVnd,
                priceSource,
                overrideReason,
                finalQty,
                lineTotalVnd,
                true,
                null));
        }

        if (outOfStock.Count > 0)
        {
            var ordered = outOfStock.OrderBy(x => x.VariantId).ToArray();
            return CheckoutCalculationResult.Failure("out_of_stock", "Một số sản phẩm không đủ tồn kho.", ordered);
        }

        long discountVnd = 0;
        Guid? appliedVoucherId = null;
        string? discountLabel = null;

        if (!string.IsNullOrWhiteSpace(input.Discount.ReferralCode))
            return CheckoutCalculationResult.Failure("referral_not_available", "Mã giới thiệu chưa khả dụng ở giai đoạn hiện tại.");

        if (input.Discount.Voucher is { } voucherInput)
        {
            var voucherResult = EvaluateVoucher(voucherInput, subtotalVnd, input.EvaluatedAt);
            if (!voucherResult.IsSuccess)
                return CheckoutCalculationResult.Failure(voucherResult.ErrorCode!, voucherResult.ErrorMessage!);
            discountVnd = voucherResult.DiscountVnd;
            appliedVoucherId = voucherResult.VoucherId;
            discountLabel = voucherResult.Label;
        }
        else if (input.Discount.Manual is { } manual)
        {
            if (input.Mode != CheckoutMode.AdminOrder)
                return CheckoutCalculationResult.Failure("validation_failed", "Chỉ đơn quản trị mới được nhập giảm giá tay.");
            if (manual.DiscountVnd < 0 || manual.DiscountVnd > subtotalVnd)
                return CheckoutCalculationResult.Failure("validation_failed", "Giảm giá tay phải nằm trong khoảng 0 đến tiền hàng.");
            if (manual.DiscountVnd > 0 && string.IsNullOrWhiteSpace(manual.DiscountNote))
                return CheckoutCalculationResult.Failure("validation_failed", "Giảm giá tay bắt buộc có ghi chú.");
            if (!manual.ActorHasCostsRead && (Int128)manual.DiscountVnd * 100 > (Int128)subtotalVnd * limits.MaxStaffDiscountPercent)
                return CheckoutCalculationResult.Failure("discount_requires_owner", "Mức giảm giá vượt giới hạn nhân viên.");

            discountVnd = manual.DiscountVnd;
            discountLabel = string.IsNullOrWhiteSpace(manual.DiscountNote) ? null : manual.DiscountNote.Trim();
        }

        var afterDiscountVnd = checked(subtotalVnd - discountVnd);
        var computedShippingVnd = input.Shipping.FreeThresholdVnd is { } threshold && afterDiscountVnd >= threshold
            ? 0L
            : input.Shipping.FlatFeeVnd;
        var shippingFeeVnd = input.Mode == CheckoutMode.AdminOrder && input.Shipping.AdminOverrideShippingFeeVnd is { } customShipping
            ? customShipping
            : computedShippingVnd;
        var totalVnd = checked(afterDiscountVnd + shippingFeeVnd);

        return new(
            true,
            null,
            null,
            pricedLines,
            unavailableLines,
            subtotalVnd,
            discountVnd,
            appliedVoucherId,
            discountLabel,
            shippingFeeVnd,
            totalVnd,
            warnings,
            []);
    }

    private static (bool IsSuccess, string? ErrorCode, string? ErrorMessage, long DiscountVnd, Guid? VoucherId, string? Label) EvaluateVoucher(
        VoucherDiscountInput input,
        long subtotalVnd,
        DateTimeOffset evaluatedAt)
    {
        // Strict BR-PRC-04 precedence:
        // 1. voucher_not_found -> 2. voucher_disabled -> 3. voucher_not_started / voucher_expired
        // -> 4. voucher_usage_exhausted -> 5. voucher_not_for_customer -> 6. voucher_customer_limit -> 7. voucher_min_order
        var v = input.Voucher;
        if (v is null)
            return (false, "voucher_not_found", "Không tìm thấy mã giảm giá.", 0, null, null);
        if (!string.Equals(v.Status, "active", StringComparison.Ordinal))
            return (false, "voucher_disabled", "Mã giảm giá đã bị tắt.", 0, null, null);
        if (evaluatedAt < v.StartsAt)
            return (false, "voucher_not_started", "Mã giảm giá chưa đến thời gian áp dụng.", 0, null, null);
        if (evaluatedAt >= v.EndsAt)
            return (false, "voucher_expired", "Mã giảm giá đã hết hạn.", 0, null, null);
        if (v.UsageLimit is { } usageLimit && v.UsedCount >= usageLimit)
            return (false, "voucher_usage_exhausted", "Mã giảm giá đã hết lượt sử dụng.", 0, null, null);
        if (v.BoundCustomerId is { } bound && bound != input.CustomerId)
            return (false, "voucher_not_for_customer", "Mã giảm giá không áp dụng cho khách hàng này.", 0, null, null);
        if (input.CustomerId is not null && input.CustomerAppliedCount >= v.PerCustomerLimit)
            return (false, "voucher_customer_limit", "Khách hàng đã dùng hết lượt của mã giảm giá này.", 0, null, null);
        if (subtotalVnd < v.MinOrderVnd)
            return (false, "voucher_min_order", "Đơn hàng chưa đạt giá trị tối thiểu để áp mã.", 0, null, null);

        long discountVnd;
        if (string.Equals(v.DiscountType, "fixed", StringComparison.Ordinal))
        {
            discountVnd = Math.Min(v.DiscountValue, subtotalVnd);
        }
        else if (string.Equals(v.DiscountType, "percent", StringComparison.Ordinal))
        {
            var raw = checked((long)((Int128)subtotalVnd * v.DiscountValue / 100));
            var capped = v.MaxDiscountVnd is { } max ? Math.Min(raw, max) : raw;
            discountVnd = Math.Min(capped, subtotalVnd);
        }
        else
        {
            return (false, "validation_failed", "Loại giảm giá của voucher không hợp lệ.", 0, null, null);
        }

        // GAP-P205-01 = A: discount == 0 cannot satisfy ck_voucher_redemptions_amount_vnd (amount_vnd > 0).
        if (discountVnd <= 0)
            return (false, "voucher_min_order", "Giá trị đơn hàng quá nhỏ để nhận mức giảm tối thiểu.", 0, null, null);

        return (true, null, null, discountVnd, v.Id, v.Code);
    }
}
