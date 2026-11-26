using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Payments;

public sealed record OrderPaymentLedgerTotals(
    long ConfirmedPaidVnd,
    long RefundedVnd,
    long OverpaymentRefundedVnd,
    long ReturnGoodsRefundedVnd,
    long AdjustmentGoodsRefundedVnd,
    long ShippingRefundedVnd);

public interface IOrderPaymentLedgerReader
{
    Task<OrderPaymentLedgerTotals> GetOrderLedgerTotalsAsync(Guid orderId, CancellationToken ct);
}

public sealed record PaymentLookupForVoid(Guid Id, Guid OrderId);

public sealed record LockedPaymentRecord(
    Guid Id,
    Guid OrderId,
    string Method,
    string Provider,
    long AmountVnd,
    string Status,
    Guid? CodSettlementId,
    DateTimeOffset? ConfirmedAt);

public sealed record NewPaymentRecord(
    Guid Id,
    Guid OrderId,
    string Method,
    string Provider,
    long AmountVnd,
    string Status,
    string? BankReference,
    DateTimeOffset ReceivedAt,
    string? Note,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    Guid ConfirmedBy,
    DateTimeOffset ConfirmedAt);

public interface IPaymentRepository
{
    Task<bool> ExistsActiveBankReferenceAsync(string provider, string bankReference, CancellationToken ct);
    Task<bool> TryInsertWithSavepointAsync(NewPaymentRecord payment, CancellationToken ct);
    Task<PaymentLookupForVoid?> FindOrderForVoidAsync(Guid paymentId, CancellationToken ct);
    Task<LockedPaymentRecord?> LoadForUpdateAsync(Guid paymentId, CancellationToken ct);
    Task VoidAsync(Guid paymentId, string voidReason, CancellationToken ct);
}

public sealed record NewRefundRecord(
    Guid Id,
    Guid OrderId,
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

// R-P201-06: sales.refunds is strictly append-only; no update or delete methods may exist on this port.
public interface IRefundRepository
{
    Task<bool> TryInsertWithSavepointAsync(NewRefundRecord refund, CancellationToken ct);
    Task<long> SumRefundedForOrderAsync(Guid orderId, CancellationToken ct);
}

public sealed record LockedReturnRequestForRefund(
    Guid Id,
    string Code,
    Guid OrderId,
    string Status,
    string? Resolution,
    IReadOnlyList<ReturnLineRefundInput> Lines);

public interface IReturnRefundPort
{
    Task<LockedReturnRequestForRefund?> LoadForUpdateAsync(Guid orderId, Guid returnRequestId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> GetPriorRefundedQuantitiesByOrderLineAsync(
        Guid orderId,
        Guid excludeReturnRequestId,
        CancellationToken ct);
    Task<bool> HasOtherApprovedRefundReturnAsync(
        Guid orderId,
        Guid? excludeReturnRequestId,
        CancellationToken ct);
    Task MarkResolvedAsync(Guid returnRequestId, DateTimeOffset resolvedAt, CancellationToken ct);
}

// Extension point for P2-10 / P2-11 / P5-05 (same transaction as refund creation).
public interface IRefundRecordedHook
{
    Task OnRefundRecordedAsync(
        Order order,
        Guid refundId,
        Guid? returnRequestId,
        string kind,
        long amountVnd,
        long shippingAmountVnd,
        OrderPaymentCalculation calculation,
        CancellationToken ct);
}

public interface IPaymentQueries
{
    Task<AwaitingPaymentsPage> GetAwaitingAsync(
        string type,
        string? carrier,
        int page,
        int pageSize,
        CancellationToken ct);
}

// Intentionally omits free-text note/bankReference so platform.idempotency_keys.response_body never stores PII (NFR-04).
public sealed record PaymentRecordedView(
    Guid PaymentId,
    Guid OrderId,
    string OrderCode,
    string Method,
    string Provider,
    string Status,
    long AmountVnd,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ConfirmedAt,
    string OrderStatus,
    string OrderPaymentStatus,
    long PaidVnd,
    long RefundedVnd,
    long NetPaidVnd,
    bool IsOverpaid,
    bool NeedsRefund,
    bool AutoConfirmed,
    string OrderRowVersion);

public sealed record PaymentVoidedView(
    Guid PaymentId,
    Guid OrderId,
    string OrderCode,
    string Status,
    long AmountVnd,
    string OrderStatus,
    string OrderPaymentStatus,
    long PaidVnd,
    long RefundedVnd,
    long NetPaidVnd,
    bool IsOverpaid,
    bool NeedsRefund,
    string OrderRowVersion);

// Intentionally omits free-text reason/bankReference so platform.idempotency_keys.response_body never stores PII (NFR-04).
public sealed record RefundRecordedView(
    Guid RefundId,
    Guid OrderId,
    string OrderCode,
    Guid? ReturnRequestId,
    string? ReturnRequestStatus,
    string Kind,
    string Method,
    long AmountVnd,
    long ShippingAmountVnd,
    DateTimeOffset RefundedAt,
    string OrderStatus,
    string OrderPaymentStatus,
    long PaidVnd,
    long RefundedVnd,
    long NetPaidVnd,
    bool IsOverpaid,
    bool NeedsRefund,
    string OrderRowVersion);

public sealed record AwaitingBankTransferItem(
    Guid OrderId,
    string OrderCode,
    string Origin,
    string SalesChannel,
    string OrderStatus,
    string PaymentStatus,
    long TotalVnd,
    long PaidVnd,
    long EffectivePaidVnd,
    long RemainingVnd,
    DateTimeOffset PlacedAt,
    DateTimeOffset? PaymentDueAt);

public sealed record AwaitingCodPaymentItem(
    Guid PaymentId,
    Guid OrderId,
    string OrderCode,
    long AmountVnd,
    string? CarrierName,
    string? TrackingCode,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset CreatedAt);

public sealed record AwaitingPaymentsPage(
    string Type,
    int Page,
    int PageSize,
    long TotalItems,
    long TotalAmountVnd,
    IReadOnlyList<AwaitingBankTransferItem> BankTransferItems,
    IReadOnlyList<AwaitingCodPaymentItem> CodItems);
