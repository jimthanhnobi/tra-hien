using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Crm;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

public sealed record OrderActor(
    Guid Id,
    bool IsOwner,
    bool HasCostsRead,
    bool HasOverridePrice);

public static class AdminOrderFlags
{
    public const string NeedsRefund = "needs_refund";
    public const string Overpaid = "overpaid";
    public const string Expiring = "expiring";

    public static readonly string[] All = [NeedsRefund, Overpaid, Expiring];

    public static bool IsValid(string? value) => value is NeedsRefund or Overpaid or Expiring;
}

public static class AdminOrderSortOptions
{
    public const string PlacedAtDesc = "placedAt:desc";
    public const string PlacedAtAsc = "placedAt:asc";
    public const string TotalVndDesc = "totalVnd:desc";
    public const string TotalVndAsc = "totalVnd:asc";
    public const string DeliveredAtDesc = "deliveredAt:desc";
    public const string DeliveredAtAsc = "deliveredAt:asc";

    public static readonly string[] All =
    [
        PlacedAtDesc, PlacedAtAsc,
        TotalVndDesc, TotalVndAsc,
        DeliveredAtDesc, DeliveredAtAsc
    ];

    public static bool IsValid(string? value) =>
        value is PlacedAtDesc or PlacedAtAsc or TotalVndDesc or TotalVndAsc or DeliveredAtDesc or DeliveredAtAsc;
}

public sealed record AdminOrderListFilter(
    int Page,
    int PageSize,
    string Sort,
    string? Status,
    string? PaymentStatus,
    string? Channel,
    string? Origin,
    DateTime? FromUtc,
    DateTime? ToUtc,
    bool ToIsExclusive,
    string? Q,
    string? PhoneSearchDigits,
    Guid? CustomerId,
    string? Flag);

// Intentionally omits all cost fields (unitCostVnd / avgCostVnd) for all roles.
public sealed record AdminOrderListItemView(
    Guid Id,
    string Code,
    Guid CustomerId,
    string? CustomerCode,
    string Origin,
    string SalesChannel,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    DateTimeOffset? PaymentDueAt,
    string ContactName,
    string ContactPhone,
    string ReceiverName,
    string ReceiverPhone,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    long PaidVnd,
    long RefundedVnd,
    long NetPaidVnd,
    bool IsOverpaid,
    bool NeedsRefund,
    bool IsExpiring,
    bool IsBackfilled,
    int ItemCount,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? CancelledAt,
    string RowVersion);

// ASSUMPTION-P206-04 = A: stored in platform.idempotency_keys.response_body; strictly 0 PII and 0 cost fields (18 properties).
public sealed record AdminOrderCreatedView(
    Guid Id,
    string Code,
    Guid CustomerId,
    string CustomerCode,
    string Origin,
    string SalesChannel,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    bool IsBackfilled,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DeliveredAt,
    string RowVersion)
{
    public static AdminOrderCreatedView From(Order order, string customerCode) => new(
        order.Id,
        order.Code,
        order.CustomerId,
        customerCode,
        order.Origin,
        order.SalesChannel,
        order.Status,
        order.PaymentMethod,
        order.PaymentStatus,
        order.SubtotalVnd,
        order.DiscountVnd,
        order.ShippingFeeVnd,
        order.TotalVnd,
        order.IsBackfilled,
        order.PlacedAt,
        order.ConfirmedAt,
        order.DeliveredAt,
        CatalogVersions.Encode(order.Version));
}

public sealed record AdminOrderWarningView(string Code, string Message);

public sealed record AdminOrderLineView(
    Guid Id,
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? VariantName,
    int Quantity,
    long ListPriceVnd,
    long UnitPriceVnd,
    string PriceSource,
    string? PriceOverrideReason,
    long LineTotalVnd,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UnitCostVnd,
    int ReturnedQuantity);

public sealed record AdminOrderPaymentView(
    Guid Id,
    string Method,
    string Provider,
    long AmountVnd,
    string Status,
    string? BankReference,
    DateTimeOffset? ReceivedAt,
    Guid? CodSettlementId,
    string? Note,
    string? VoidReason,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    Guid? ConfirmedBy,
    DateTimeOffset? ConfirmedAt);

public sealed record AdminOrderRefundView(
    Guid Id,
    Guid? ReturnRequestId,
    string Kind,
    long AmountVnd,
    long ShippingAmountVnd,
    string Method,
    string? BankReference,
    string Reason,
    DateTimeOffset RefundedAt,
    Guid CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record AdminOrderReturnLineView(
    Guid Id,
    Guid OrderLineId,
    int Quantity,
    bool Restockable);

public sealed record AdminOrderReturnView(
    Guid Id,
    string Code,
    string Reason,
    string Description,
    string Status,
    string? Resolution,
    string? DecisionNote,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ResolvedAt,
    Guid? CreatedBy,
    IReadOnlyList<AdminOrderReturnLineView> Lines);

public sealed record AdminOrderHistoryView(
    Guid Id,
    string? FromStatus,
    string ToStatus,
    string ActorType,
    Guid? ActorId,
    string? Reason,
    DateTimeOffset OccurredAt);

public sealed record AdminOrderDetailView(
    Guid Id,
    string Code,
    Guid CustomerId,
    string? CustomerCode,
    string Origin,
    string SalesChannel,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    DateTimeOffset? PaymentDueAt,
    string ContactName,
    string ContactPhone,
    string? ContactEmail,
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string ProvinceName,
    string? WardCode,
    string WardName,
    string AddressLine,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    string? DiscountNote,
    Guid? VoucherId,
    string? ReferralCode,
    string AcquisitionSource,
    bool IsFirstPurchase,
    string? CustomerNote,
    string? InternalNote,
    string? StatusReason,
    long PaidVnd,
    long RefundedVnd,
    long OverpaymentRefundedVnd,
    long EffectivePaidVnd,
    long NetPaidVnd,
    long RemainingToPayVnd,
    long OverpaidVnd,
    bool IsOverpaid,
    bool NeedsRefund,
    bool IsExpiring,
    bool IsBackfilled,
    Guid? CreatedByStaffId,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RowVersion,
    IReadOnlyList<AdminOrderLineView> Lines,
    IReadOnlyList<AdminOrderPaymentView> Payments,
    IReadOnlyList<AdminOrderRefundView> Refunds,
    IReadOnlyList<ShipmentDetailView> Shipments,
    IReadOnlyList<AdminOrderReturnView> Returns,
    IReadOnlyList<AdminOrderHistoryView> History,
    IReadOnlyList<AdminOrderWarningView> Warnings);

// BR-ORD-12: strictly excludes unitCostVnd, avgCostVnd, internalNote, discountNote, priceOverrideReason.
public sealed record PackingSlipLine(
    string Sku,
    string ProductName,
    string? VariantName,
    int Quantity,
    long UnitPriceVnd,
    long LineTotalVnd);

public sealed record PackingSlipDocument(
    string ShopName,
    string? ShopHotline,
    string? ShopAddress,
    string OrderCode,
    DateTimeOffset PlacedAt,
    string ReceiverName,
    string ReceiverPhone,
    string AddressLine,
    string WardName,
    string ProvinceName,
    string? CustomerNote,
    long SubtotalVnd,
    long DiscountVnd,
    long ShippingFeeVnd,
    long TotalVnd,
    long CodAmountVnd,
    IReadOnlyList<PackingSlipLine> Lines);

public interface IAdminOrderQueries
{
    Task<CatalogPage<AdminOrderListItemView>> ListAsync(AdminOrderListFilter filter, CancellationToken ct);
    Task<AdminOrderDetailView?> GetDetailAsync(
        Guid orderId,
        bool includeCosts,
        IReadOnlyList<AdminOrderWarningView>? warnings,
        CancellationToken ct);
    Task<PackingSlipDocument?> GetPackingSlipAsync(Guid orderId, CancellationToken ct);
}

public static class AdminOrderFilterParser
{
    public static bool TryParseFromFilter(string? raw, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!CatalogInput.WithoutNul(raw) || raw.Length > 40) return false;

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            utc = BusinessCalendar.ToUtc(date, TimeOnly.MinValue).UtcDateTime;
            return true;
        }

        if (!HasExplicitUtcOffset(raw)) return false;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        return false;
    }

    public static bool TryParseToFilter(string? raw, out DateTime? utc, out bool isExclusive)
    {
        utc = null;
        isExclusive = false;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!CatalogInput.WithoutNul(raw) || raw.Length > 40) return false;

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            // Whole Vietnam business day inclusive: [from, to + 1d 00:00 VN).
            utc = BusinessCalendar.ToUtc(date.AddDays(1), TimeOnly.MinValue).UtcDateTime;
            isExclusive = true;
            return true;
        }

        if (!HasExplicitUtcOffset(raw)) return false;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            utc = dto.UtcDateTime;
            isExclusive = false;
            return true;
        }

        return false;
    }

    private static bool HasExplicitUtcOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z')) return true;
        return value.Length >= 6
            && value[^6] is '+' or '-'
            && value[^3] == ':'
            && char.IsAsciiDigit(value[^5])
            && char.IsAsciiDigit(value[^4])
            && char.IsAsciiDigit(value[^2])
            && char.IsAsciiDigit(value[^1]);
    }
}

// ASSUMPTION-P206-04 = A: strict 18-property allowlist for platform.idempotency_keys.response_body (0 PII, 0 cost fields).
public static class AdminOrderIdempotencyPolicy
{
    public static bool SafeCreateOrderResponse(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 18
        && IsNonEmptyGuid(value, "id")
        && IsValidString(value, "code", OrderCode.IsValid)
        && IsNonEmptyGuid(value, "customerId")
        && IsValidString(value, "customerCode", CustomerRules.IsValidCustomerCode)
        && IsValidString(value, "origin", OrderOrigin.IsValid)
        && IsValidString(value, "salesChannel", SalesChannel.IsValidForAdmin)
        && IsValidString(value, "status", OrderStatus.IsValid)
        && IsValidString(value, "paymentMethod", OrderPaymentMethod.IsValid)
        && IsValidString(value, "paymentStatus", OrderPaymentStatus.IsValid)
        && IsNonNegativeInt64(value, "subtotalVnd")
        && IsNonNegativeInt64(value, "discountVnd")
        && IsNonNegativeInt64(value, "shippingFeeVnd")
        && IsNonNegativeInt64(value, "totalVnd")
        && IsBoolean(value, "isBackfilled")
        && IsDateTimeOffset(value, "placedAt")
        && IsNullableDateTimeOffset(value, "confirmedAt")
        && IsNullableDateTimeOffset(value, "deliveredAt")
        && IsCanonicalRowVersion(value, "rowVersion");

    private static bool IsNonEmptyGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && prop.TryGetGuid(out var parsed)
        && parsed != Guid.Empty;

    private static bool IsValidString(JsonElement root, string name, Func<string?, bool> predicate) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && predicate(prop.GetString());

    private static bool IsNonNegativeInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt64(out var parsed)
        && parsed >= 0;

    private static bool IsBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && prop.TryGetDateTimeOffset(out _);

    private static bool IsNullableDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && (prop.ValueKind == JsonValueKind.Null
            || (prop.ValueKind == JsonValueKind.String && prop.TryGetDateTimeOffset(out _)));

    private static bool IsCanonicalRowVersion(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && CatalogVersions.TryDecode(prop.GetString(), out var decoded)
        && prop.ValueEquals(CatalogVersions.Encode(decoded));
}
