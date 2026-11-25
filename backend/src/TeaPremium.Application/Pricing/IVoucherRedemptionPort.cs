using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Pricing;

public sealed record OrderVoucherRedemption(
    Guid Id,
    Guid VoucherId,
    Guid OrderId,
    Guid CustomerId,
    long AmountVnd,
    string Status);

public sealed record VoucherApplyResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static VoucherApplyResult Success() => new(true, null, null);
    public static VoucherApplyResult Failure(string code, string message) => new(false, code, message);
}

// BR-PRC-04/05 + G12=A + R-P201-02: voucher lookup, row locking (L5: pricing.vouchers ORDER BY id FOR NO KEY UPDATE),
// atomic redemption upsert by order_id (enforcing usage_limit & per_customer_limit under lock), amount sync, and revert.
public interface IVoucherRedemptionPort
{
    Task<VoucherSnapshot?> GetByCodeAsync(string code, CancellationToken ct);
    Task<VoucherSnapshot?> LockByCodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, VoucherSnapshot>> LockByIdsAsync(IReadOnlyCollection<Guid> voucherIds, CancellationToken ct);
    Task<OrderVoucherRedemption?> GetByOrderIdAsync(Guid orderId, CancellationToken ct);
    Task<int> CountAppliedByCustomerAsync(Guid voucherId, Guid customerId, Guid? excludeOrderId, CancellationToken ct);
    Task<VoucherApplyResult> UpsertApplyAsync(Guid voucherId, Guid orderId, Guid customerId, long amountVnd, CancellationToken ct);
    Task UpdateAppliedAmountAsync(Guid orderId, long amountVnd, CancellationToken ct);
    Task RevertForOrderAsync(Guid orderId, CancellationToken ct);
}
