using TeaPremium.Domain.Orders;
using TeaPremium.Domain.Pricing;

namespace TeaPremium.UnitTests;

public sealed class CheckoutCalculatorTests
{
    private static readonly Guid ProductId = Guid.Parse("00000000-0000-0000-0000-000000002001");
    private static readonly Guid VariantA = Guid.Parse("00000000-0000-0000-0000-000000002002");
    private static readonly Guid VariantB = Guid.Parse("00000000-0000-0000-0000-000000002003");
    private static readonly Guid CustomerA = Guid.Parse("00000000-0000-0000-0000-000000002004");
    private static readonly Guid CustomerB = Guid.Parse("00000000-0000-0000-0000-000000002005");
    private static readonly Guid VoucherId = Guid.Parse("00000000-0000-0000-0000-000000002006");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly ShippingRuleInput DefaultShipping = new(30_000L, 1_000_000L);

    private static CheckoutLineInput Line(
        Guid? variantId = null,
        string sku = "AM-TN-0001",
        bool productActive = true,
        bool variantActive = true,
        long? basePrice = 500_000L,
        IReadOnlyList<ScheduledPrice>? promos = null,
        bool unique = false,
        int available = 5,
        int requested = 1,
        long? overridePrice = null,
        string? overrideReason = null) => new(
            variantId ?? VariantA,
            ProductId,
            sku,
            "Am Tu Sa",
            "180ml",
            productActive,
            variantActive,
            basePrice,
            promos ?? [],
            unique,
            available,
            requested,
            overridePrice,
            overrideReason);

    private static VoucherSnapshot ActiveVoucher(
        string discountType = "fixed",
        long discountValue = 100_000L,
        long? maxDiscount = null,
        long minOrder = 0L,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        int? usageLimit = null,
        int perCustomerLimit = 1,
        Guid? boundCustomerId = null,
        int usedCount = 0,
        string status = "active") => new(
            VoucherId,
            "TEA100",
            "manual",
            discountType,
            discountValue,
            maxDiscount,
            minOrder,
            startsAt ?? Now.AddDays(-1),
            endsAt ?? Now.AddDays(1),
            usageLimit,
            perCustomerLimit,
            boundCustomerId,
            usedCount,
            status);

    [Fact]
    public void WebQuoteExcludesUnavailableLinesClampsQuantityAndAppliesActiveSalePrice()
    {
        var promo = new ScheduledPrice(420_000L, Now.AddHours(-1), Now.AddHours(1));
        var result = CheckoutCalculator.Calculate(new(
            CheckoutMode.WebQuote,
            Now,
            [
                Line(VariantA, "SKU-A", basePrice: 500_000L, promos: [promo], available: 2, requested: 5),
                Line(VariantB, "SKU-B", productActive: false, basePrice: 300_000L, available: 5, requested: 1)
            ],
            CheckoutDiscountInput.None,
            DefaultShipping));

        Assert.True(result.IsSuccess);
        Assert.Equal(840_000L, result.SubtotalVnd);
        Assert.Equal(30_000L, result.ShippingFeeVnd);
        Assert.Equal(870_000L, result.TotalVnd);
        var availableLine = result.PricedLines.Single(x => x.VariantId == VariantA);
        Assert.Equal(OrderPriceSource.Sale, availableLine.PriceSource);
        Assert.Equal(2, availableLine.Quantity);
        Assert.Equal(500_000L, availableLine.ListPriceVnd);
        Assert.Equal(420_000L, availableLine.UnitPriceVnd);
        Assert.Single(result.UnavailableLines);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void WebPlaceOrderRejectsInactiveProductOrVariantAndCollectsAllOutOfStockLines()
    {
        // ASSUMPTION-P205-02 = A
        var inactiveProduct = CheckoutCalculator.Calculate(new(
            CheckoutMode.WebPlaceOrder, Now, [Line(productActive: false)], CheckoutDiscountInput.None, DefaultShipping));
        Assert.False(inactiveProduct.IsSuccess);
        Assert.Equal("cart_has_unavailable_items", inactiveProduct.ErrorCode);

        var outOfStock = CheckoutCalculator.Calculate(new(
            CheckoutMode.WebPlaceOrder,
            Now,
            [
                Line(VariantA, "SKU-A", available: 1, requested: 2),
                Line(VariantB, "SKU-B", available: 0, requested: 1)
            ],
            CheckoutDiscountInput.None,
            DefaultShipping));
        Assert.False(outOfStock.IsSuccess);
        Assert.Equal("out_of_stock", outOfStock.ErrorCode);
        Assert.Equal(2, outOfStock.OutOfStockItems.Count);
    }

    [Fact]
    public void AdminOrderEnforcesSellabilityUniquePieceAndOverrideReason()
    {
        // ASSUMPTION-P205-02 = A: draft product or null price -> variant_not_sellable
        var draftProduct = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(productActive: false)], CheckoutDiscountInput.None, DefaultShipping));
        Assert.Equal("variant_not_sellable", draftProduct.ErrorCode);

        var nullPrice = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: null)], CheckoutDiscountInput.None, DefaultShipping));
        Assert.Equal("variant_not_sellable", nullPrice.ErrorCode);

        var uniqueTwo = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(unique: true, requested: 2)], CheckoutDiscountInput.None, DefaultShipping));
        Assert.Equal("unique_piece_violation", uniqueTwo.ErrorCode);

        var shortReason = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(overridePrice: 400_000L, overrideReason: "ab")], CheckoutDiscountInput.None, DefaultShipping));
        Assert.Equal("validation_failed", shortReason.ErrorCode);

        var validOverride = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(requested: 2, overridePrice: 450_000L, overrideReason: "Khach VIP")], CheckoutDiscountInput.None, DefaultShipping));
        Assert.True(validOverride.IsSuccess);
        Assert.Equal(900_000L, validOverride.SubtotalVnd);
        Assert.Equal(OrderPriceSource.Override, Assert.Single(validOverride.PricedLines).PriceSource);
    }

    [Fact]
    public void VoucherEnforcesStrictBrPrc04ErrorPrecedence()
    {
        // 1. not found
        Assert.Equal("voucher_not_found", CalcWithVoucher(null, CustomerA, 0, 500_000L).ErrorCode);

        // 2. disabled beats expired & min_order
        var disabledAndExpired = ActiveVoucher(status: "disabled", endsAt: Now.AddMinutes(-1), minOrder: 9_999_999L);
        Assert.Equal("voucher_disabled", CalcWithVoucher(disabledAndExpired, CustomerA, 99, 100_000L).ErrorCode);

        // 3a. not started beats usage_exhausted
        var notStarted = ActiveVoucher(startsAt: Now.AddHours(1), endsAt: Now.AddHours(2), usageLimit: 1, usedCount: 1);
        Assert.Equal("voucher_not_started", CalcWithVoucher(notStarted, CustomerA, 99, 100_000L).ErrorCode);

        // 3b. expired (endsAt == Now is expired since starts_at <= t < ends_at)
        var expired = ActiveVoucher(endsAt: Now, usageLimit: 1, usedCount: 1);
        Assert.Equal("voucher_expired", CalcWithVoucher(expired, CustomerA, 99, 100_000L).ErrorCode);

        // 4. usage exhausted beats bound customer
        var exhausted = ActiveVoucher(usageLimit: 5, usedCount: 5, boundCustomerId: CustomerB);
        Assert.Equal("voucher_usage_exhausted", CalcWithVoucher(exhausted, CustomerA, 99, 100_000L).ErrorCode);

        // 5. bound customer beats per-customer limit
        var bound = ActiveVoucher(boundCustomerId: CustomerB, perCustomerLimit: 1);
        Assert.Equal("voucher_not_for_customer", CalcWithVoucher(bound, CustomerA, 99, 100_000L).ErrorCode);

        // 6. per-customer limit beats min_order
        var perCust = ActiveVoucher(perCustomerLimit: 1, minOrder: 9_999_999L);
        Assert.Equal("voucher_customer_limit", CalcWithVoucher(perCust, CustomerA, 1, 100_000L).ErrorCode);

        // 7. min order
        var minOrder = ActiveVoucher(minOrder: 1_000_000L);
        Assert.Equal("voucher_min_order", CalcWithVoucher(minOrder, CustomerA, 0, 999_999L).ErrorCode);
    }

    [Fact]
    public void VoucherPercentUsesFloorRoundingCapsAtMaxAndZeroDiscountReturnsVoucherMinOrder()
    {
        // floor(155 * 10 / 100) == 15 (not 16)
        var pct = ActiveVoucher(discountType: "percent", discountValue: 10, maxDiscount: null, minOrder: 0);
        var res = CalcWithVoucher(pct, CustomerA, 0, 155L);
        Assert.True(res.IsSuccess);
        Assert.Equal(15L, res.DiscountVnd);

        // Capped at max_discount_vnd
        var capped = ActiveVoucher(discountType: "percent", discountValue: 50, maxDiscount: 200_000L, minOrder: 0);
        var resCapped = CalcWithVoucher(capped, CustomerA, 0, 1_000_000L);
        Assert.True(resCapped.IsSuccess);
        Assert.Equal(200_000L, resCapped.DiscountVnd);

        // GAP-P205-01 = A: floor(50 * 1 / 100) == 0 -> voucher_min_order
        var tinyPct = ActiveVoucher(discountType: "percent", discountValue: 1, maxDiscount: null, minOrder: 0);
        var resZero = CalcWithVoucher(tinyPct, CustomerA, 0, 50L);
        Assert.False(resZero.IsSuccess);
        Assert.Equal("voucher_min_order", resZero.ErrorCode);
    }

    [Fact]
    public void ReferralCodeAndAdminManualDiscountEnforceRulesAndStaffCap()
    {
        var referral = CheckoutCalculator.Calculate(new(
            CheckoutMode.WebPlaceOrder, Now, [Line()], new(ReferralCode: "REF123"), DefaultShipping));
        Assert.Equal("referral_not_available", referral.ErrorCode);

        var twoDiscounts = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line()],
            new(Voucher: new("TEA100", ActiveVoucher(), CustomerA, 0), Manual: new(10_000L, "Note", true)),
            DefaultShipping));
        Assert.Equal("validation_failed", twoDiscounts.ErrorCode);

        // Staff (no costs.read): 10% of 500_000 == 50_000 allowed; 50_001 -> discount_requires_owner
        var atCap = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 500_000L)],
            new(Manual: new(50_000L, "Giam 10%", ActorHasCostsRead: false)),
            DefaultShipping));
        Assert.True(atCap.IsSuccess);
        Assert.Equal(50_000L, atCap.DiscountVnd);

        var overCap = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 500_000L)],
            new(Manual: new(50_001L, "Giam qua han muc", ActorHasCostsRead: false)),
            DefaultShipping));
        Assert.Equal("discount_requires_owner", overCap.ErrorCode);

        var ownerOverCap = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 500_000L)],
            new(Manual: new(200_000L, "Chu shop giam", ActorHasCostsRead: true)),
            DefaultShipping));
        Assert.True(ownerOverCap.IsSuccess);
        Assert.Equal(200_000L, ownerOverCap.DiscountVnd);
    }

    [Fact]
    public void ShippingFeeUsesAfterDiscountBaseAndMatchesOrderTotalInvariant()
    {
        // base = 1_050_000 - 100_000 = 950_000 < 1_000_000 -> flat 30_000
        var belowThreshold = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 1_050_000L)],
            new(Manual: new(100_000L, "Giam", true)),
            new(30_000L, 1_000_000L)));
        Assert.True(belowThreshold.IsSuccess);
        Assert.Equal(30_000L, belowThreshold.ShippingFeeVnd);
        Assert.Equal(belowThreshold.SubtotalVnd - belowThreshold.DiscountVnd + belowThreshold.ShippingFeeVnd, belowThreshold.TotalVnd);

        // base == 1_000_000 -> free ship
        var atThreshold = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 1_100_000L)],
            new(Manual: new(100_000L, "Giam", true)),
            new(30_000L, 1_000_000L)));
        Assert.True(atThreshold.IsSuccess);
        Assert.Equal(0L, atThreshold.ShippingFeeVnd);
        Assert.Equal(1_000_000L, atThreshold.TotalVnd);

        // null threshold -> always flat fee unless Admin overrides
        var adminOverrideShip = CheckoutCalculator.Calculate(new(
            CheckoutMode.AdminOrder, Now, [Line(basePrice: 500_000L)],
            CheckoutDiscountInput.None,
            new(30_000L, null, AdminOverrideShippingFeeVnd: 15_000L)));
        Assert.True(adminOverrideShip.IsSuccess);
        Assert.Equal(15_000L, adminOverrideShip.ShippingFeeVnd);
        Assert.Equal(515_000L, adminOverrideShip.TotalVnd);
    }

    private static CheckoutCalculationResult CalcWithVoucher(VoucherSnapshot? voucher, Guid customerId, int appliedCount, long price) =>
        CheckoutCalculator.Calculate(new(
            CheckoutMode.WebPlaceOrder,
            Now,
            [Line(basePrice: price)],
            new(Voucher: new("TEA100", voucher, customerId, appliedCount)),
            DefaultShipping));
}
