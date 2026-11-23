// P2-01 persistence only; business behavior and [Audited] policies belong to P2-02+.
namespace TeaPremium.Infrastructure.Persistence;

public sealed class Customer
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? PhoneE164 { get; set; }
    public string? Email { get; set; }
    public string? ZaloName { get; set; }
    public string CustomerGroup { get; set; } = null!;
    public bool IsVip { get; set; }
    public string Origin { get; set; } = null!;
    public string AcquisitionSource { get; set; } = null!;
    public Guid? ReferredByCustomerId { get; set; }
    public string ContactStatus { get; set; } = null!;
    public string? NoteSummary { get; set; }
    public bool HadLegacyPurchase { get; set; }
    public DateTime? FirstDeliveredAt { get; set; }
    public DateTime? LastDeliveredAt { get; set; }
    public int DeliveredOrdersCount { get; set; }
    public long LifetimeValueVnd { get; set; }
    public DateTime? LastContactedAt { get; set; }
    public string SearchText { get; set; } = null!;
    public Guid? MergedIntoCustomerId { get; set; }
    public DateTime? ErasedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class Tag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
}

public sealed class CustomerTag
{
    public Guid CustomerId { get; set; }
    public Guid TagId { get; set; }
}

public sealed class CustomerAddress
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? Label { get; set; }
    public string ReceiverName { get; set; } = null!;
    public string ReceiverPhone { get; set; } = null!;
    public string ProvinceCode { get; set; } = null!;
    public string? WardCode { get; set; }
    public string? WardNameText { get; set; }
    public string AddressLine { get; set; } = null!;
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CustomerAccount
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? LoginPhone { get; set; }
    public string? PasswordHash { get; set; }
    public string? Email { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? PhoneVerifiedAt { get; set; }
    public DateTime? HistoryUnlockedAt { get; set; }
    public string Status { get; set; } = null!;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public string SecurityStamp { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class CustomerLogin
{
    public string Provider { get; set; } = null!;
    public string ProviderKey { get; set; } = null!;
    public Guid AccountId { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Interaction
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid StaffId { get; set; }
    public string Channel { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string Content { get; set; } = null!;
    public DateTime OccurredAt { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? CampaignId { get; set; }
    public DateTime? FollowUpAt { get; set; }
    public DateTime? FollowUpDoneAt { get; set; }
    public string? FollowUpResult { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CustomerInterest
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string InterestType { get; set; } = null!;
    public Guid? RefId { get; set; }
    public string? PriceBand { get; set; }
    public string Source { get; set; } = null!;
    public int Score { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
}

public sealed class Consent
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Channel { get; set; } = null!;
    public string Purpose { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Source { get; set; } = null!;
    public string Evidence { get; set; } = null!;
    public DateTime CapturedAt { get; set; }
    public Guid? CapturedBy { get; set; }
}

public sealed class DataRequest
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string RequestType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Channel { get; set; } = null!;
    public string? Note { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? HandledBy { get; set; }
}

public sealed class Voucher
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string DiscountType { get; set; } = null!;
    public long DiscountValue { get; set; }
    public long? MaxDiscountVnd { get; set; }
    public long MinOrderVnd { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int? UsageLimit { get; set; }
    public int PerCustomerLimit { get; set; } = 1;
    public Guid? BoundCustomerId { get; set; }
    public int UsedCount { get; set; }
    public string Status { get; set; } = null!;
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class VoucherRedemption
{
    public Guid Id { get; set; }
    public Guid VoucherId { get; set; }
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public long AmountVnd { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public sealed class Cart
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = null!;
    public Guid? CustomerAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class CartItem
{
    public Guid CartId { get; set; }
    public Guid VariantId { get; set; }
    public int Quantity { get; set; }
    public DateTime AddedAt { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Origin { get; set; } = null!;
    public string SalesChannel { get; set; } = null!;
    public Guid CustomerId { get; set; }
    public Guid? CustomerAccountId { get; set; }
    public string Status { get; set; } = null!;
    public string PaymentMethod { get; set; } = null!;
    public string PaymentStatus { get; set; } = null!;
    public DateTime? PaymentDueAt { get; set; }
    public string ContactName { get; set; } = null!;
    public string ContactPhone { get; set; } = null!;
    public string? ContactEmail { get; set; }
    public string ReceiverName { get; set; } = null!;
    public string ReceiverPhone { get; set; } = null!;
    public string ProvinceCode { get; set; } = null!;
    public string ProvinceName { get; set; } = null!;
    public string? WardCode { get; set; }
    public string WardName { get; set; } = null!;
    public string AddressLine { get; set; } = null!;
    public long SubtotalVnd { get; set; }
    public long DiscountVnd { get; set; }
    public long ShippingFeeVnd { get; set; }
    public long TotalVnd { get; set; }
    public string? DiscountNote { get; set; }
    public Guid? VoucherId { get; set; }
    public string? ReferralCode { get; set; }
    public string AcquisitionSource { get; set; } = null!;
    public bool IsFirstPurchase { get; set; }
    public Guid? TrackingLinkId { get; set; }
    public Guid? CampaignId { get; set; }
    public string? Utm { get; set; }
    public string? CustomerNote { get; set; }
    public string? InternalNote { get; set; }
    public string? StatusReason { get; set; }
    public DateTime PlacedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceIssuedAt { get; set; }
    public bool IsBackfilled { get; set; }
    public Guid? CreatedByStaffId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid VariantId { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = null!;
    public string ProductName { get; set; } = null!;
    public string? VariantName { get; set; }
    public int Quantity { get; set; }
    public long ListPriceVnd { get; set; }
    public long UnitPriceVnd { get; set; }
    public string PriceSource { get; set; } = null!;
    public string? PriceOverrideReason { get; set; }
    public long LineTotalVnd { get; set; }
    public long? UnitCostVnd { get; set; }
    public int ReturnedQuantity { get; set; }
    public DateTime? ReviewRequestedAt { get; set; }
}

public sealed class OrderStatusHistory
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = null!;
    public string ActorType { get; set; } = null!;
    public Guid? ActorId { get; set; }
    public string? Reason { get; set; }
    public DateTime OccurredAt { get; set; }
}

public sealed class CodSettlement
{
    public Guid Id { get; set; }
    public string CarrierName { get; set; } = null!;
    public string? Reference { get; set; }
    public DateTime SettledAt { get; set; }
    public long GrossCodVnd { get; set; }
    public long FeeVnd { get; set; }
    public long NetReceivedVnd { get; set; }
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Method { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public long AmountVnd { get; set; }
    public string Status { get; set; } = null!;
    public string? BankReference { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public Guid? CodSettlementId { get; set; }
    public string? Note { get; set; }
    public string? VoidReason { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}

public sealed class ReturnRequest
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public Guid OrderId { get; set; }
    public string Reason { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? Resolution { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime RequestedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public uint Version { get; set; }
}

public sealed class ReturnLine
{
    public Guid Id { get; set; }
    public Guid ReturnRequestId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
    public bool Restockable { get; set; }
}

public sealed class ReturnEvidence
{
    public Guid ReturnRequestId { get; set; }
    public Guid AssetId { get; set; }
}

public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ReturnRequestId { get; set; }
    public long AmountVnd { get; set; }
    public string Method { get; set; } = null!;
    public string? BankReference { get; set; }
    public string Reason { get; set; } = null!;
    public DateTime RefundedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Shipment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Purpose { get; set; } = null!;
    public Guid? ReturnRequestId { get; set; }
    public string Provider { get; set; } = null!;
    public string CarrierName { get; set; } = null!;
    public string? TrackingCode { get; set; }
    public string Status { get; set; } = null!;
    public long CodAmountVnd { get; set; }
    public long? ShippingCostVnd { get; set; }
    public long? ReturnCostVnd { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PickedUpAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
}

public sealed class ShipmentEvent
{
    public Guid Id { get; set; }
    public Guid ShipmentId { get; set; }
    public string Status { get; set; } = null!;
    public string? Note { get; set; }
    public string Source { get; set; } = null!;
    public DateTime OccurredAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class Expense
{
    public Guid Id { get; set; }
    public string Category { get; set; } = null!;
    public string? Subcategory { get; set; }
    public long AmountVnd { get; set; }
    public DateOnly IncurredOn { get; set; }
    public string? Channel { get; set; }
    public Guid? CampaignId { get; set; }
    public string Description { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
