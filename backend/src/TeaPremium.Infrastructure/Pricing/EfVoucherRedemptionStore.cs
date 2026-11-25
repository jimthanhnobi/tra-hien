using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Pricing;

// BR-PRC-04/05 + G12=A + R-P201-02:
// Global lock level L5: pricing.vouchers (ORDER BY id FOR NO KEY UPDATE), always acquired AFTER
// sales.orders (L1) and catalog.products -> catalog.variants -> inventory.stock_levels (L2-L4).
public sealed class EfVoucherRedemptionStore(AppDbContext db, IClock clock) : IVoucherRedemptionPort
{
    public async Task<VoucherSnapshot?> GetByCodeAsync(string code, CancellationToken ct)
    {
        var row = await db.Set<Voucher>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Code == code, ct);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<VoucherSnapshot?> LockByCodeAsync(string code, CancellationToken ct)
    {
        RequireTransaction();
        var row = (await db.Set<Voucher>().FromSqlInterpolated(
            $"SELECT *, xmin FROM pricing.vouchers WHERE code = {code} FOR NO KEY UPDATE")
            .AsNoTracking()
            .ToArrayAsync(ct)).SingleOrDefault();
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<IReadOnlyDictionary<Guid, VoucherSnapshot>> LockByIdsAsync(
        IReadOnlyCollection<Guid> voucherIds,
        CancellationToken ct)
    {
        RequireTransaction();
        if (voucherIds.Count == 0) return new Dictionary<Guid, VoucherSnapshot>();
        var orderedIds = voucherIds.Distinct().Order().ToArray();
        var rows = await db.Set<Voucher>().FromSqlInterpolated(
            $"SELECT *, xmin FROM pricing.vouchers WHERE id = ANY({orderedIds}) ORDER BY id FOR NO KEY UPDATE")
            .AsNoTracking()
            .ToArrayAsync(ct);
        return rows.ToDictionary(x => x.Id, ToSnapshot);
    }

    public async Task<OrderVoucherRedemption?> GetByOrderIdAsync(Guid orderId, CancellationToken ct)
    {
        var row = await db.Set<VoucherRedemption>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        return row is null
            ? null
            : new(row.Id, row.VoucherId, row.OrderId, row.CustomerId, row.AmountVnd, row.Status);
    }

    public Task<int> CountAppliedByCustomerAsync(Guid voucherId, Guid customerId, Guid? excludeOrderId, CancellationToken ct)
    {
        var query = db.Set<VoucherRedemption>().AsNoTracking()
            .Where(x => x.VoucherId == voucherId && x.CustomerId == customerId && x.Status == "applied");
        if (excludeOrderId is { } excluded)
            query = query.Where(x => x.OrderId != excluded);
        return query.CountAsync(ct);
    }

    public async Task<VoucherApplyResult> UpsertApplyAsync(
        Guid voucherId,
        Guid orderId,
        Guid customerId,
        long amountVnd,
        CancellationToken ct)
    {
        RequireTransaction();

        // Defense-in-depth (F2): acquire/re-enter L5 row lock on pricing.vouchers before checking
        // status, usage_limit, and per_customer_limit so concurrent orders cannot race on per_customer_limit.
        var voucher = (await db.Set<Voucher>().FromSqlInterpolated(
            $"SELECT *, xmin FROM pricing.vouchers WHERE id = {voucherId} FOR NO KEY UPDATE")
            .AsNoTracking()
            .ToArrayAsync(ct)).SingleOrDefault();

        if (voucher is null)
            return VoucherApplyResult.Failure("voucher_not_found", "Không tìm thấy mã giảm giá.");
        if (!string.Equals(voucher.Status, "active", StringComparison.Ordinal))
            return VoucherApplyResult.Failure("voucher_disabled", "Mã giảm giá đã bị tắt.");
        if (voucher.UsageLimit is { } usageLimit && voucher.UsedCount >= usageLimit)
            return VoucherApplyResult.Failure("voucher_usage_exhausted", "Mã giảm giá đã hết lượt sử dụng.");

        var appliedByCustomer = await CountAppliedByCustomerAsync(voucherId, customerId, orderId, ct);
        if (appliedByCustomer >= voucher.PerCustomerLimit)
            return VoucherApplyResult.Failure("voucher_customer_limit", "Khách hàng đã dùng hết lượt của mã giảm giá này.");

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pricing.vouchers
               SET used_count = used_count + 1
             WHERE id = {voucherId}
               AND status = 'active'
               AND (usage_limit IS NULL OR used_count < usage_limit)
            """, ct);

        if (updated != 1)
            return VoucherApplyResult.Failure("voucher_usage_exhausted", "Mã giảm giá đã hết lượt sử dụng.");

        var now = clock.UtcNow.UtcDateTime;
        // G12=A / R-P201-02: UNIQUE(order_id) — update existing reverted row for this order instead of INSERT.
        var existing = await db.Set<VoucherRedemption>()
            .SingleOrDefaultAsync(x => x.OrderId == orderId, ct);

        if (existing is not null)
        {
            if (string.Equals(existing.Status, "applied", StringComparison.Ordinal))
                throw new InvalidOperationException("Cannot re-apply voucher on an order with an active redemption without reverting first.");

            existing.VoucherId = voucherId;
            existing.CustomerId = customerId;
            existing.AmountVnd = amountVnd;
            existing.Status = "applied";
            existing.CreatedAt = now;
        }
        else
        {
            db.Set<VoucherRedemption>().Add(new VoucherRedemption
            {
                Id = Guid.CreateVersion7(),
                VoucherId = voucherId,
                OrderId = orderId,
                CustomerId = customerId,
                AmountVnd = amountVnd,
                Status = "applied",
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return VoucherApplyResult.Success();
    }

    public async Task UpdateAppliedAmountAsync(Guid orderId, long amountVnd, CancellationToken ct)
    {
        RequireTransaction();
        var redemption = await db.Set<VoucherRedemption>()
            .SingleOrDefaultAsync(x => x.OrderId == orderId && x.Status == "applied", ct)
            ?? throw new InvalidOperationException("Missing applied voucher redemption for order amount sync.");

        if (redemption.AmountVnd == amountVnd) return;
        redemption.AmountVnd = amountVnd;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevertForOrderAsync(Guid orderId, CancellationToken ct)
    {
        RequireTransaction();
        var redemption = await db.Set<VoucherRedemption>()
            .SingleOrDefaultAsync(x => x.OrderId == orderId && x.Status == "applied", ct);
        if (redemption is null) return;

        // Acquire L5 lock on pricing.vouchers before decrementing used_count.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM pricing.vouchers WHERE id = {redemption.VoucherId} FOR NO KEY UPDATE", ct);

        redemption.Status = "reverted";
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pricing.vouchers
               SET used_count = used_count - 1
             WHERE id = {redemption.VoucherId} AND used_count > 0
            """, ct);
        if (updated != 1)
            throw new InvalidOperationException("Unable to decrement voucher used_count during order cancellation/update.");

        await db.SaveChangesAsync(ct);
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Voucher redemption operations require an active command transaction.");
    }

    private static VoucherSnapshot ToSnapshot(Voucher row) => new(
        row.Id,
        row.Code,
        row.Kind,
        row.DiscountType,
        row.DiscountValue,
        row.MaxDiscountVnd,
        row.MinOrderVnd,
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        row.UsageLimit,
        row.PerCustomerLimit,
        row.BoundCustomerId,
        row.UsedCount,
        row.Status);
}
