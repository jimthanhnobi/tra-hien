using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Pricing.Contracts;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Pricing;

// 02 §3 cross-module implementation owned by Pricing for P2-12 customer merge reassignment.
public sealed class EfPricingCustomerMergePort(AppDbContext db) : IPricingCustomerMergePort
{
    public async Task<(int MovedVouchers, int MovedRedemptions)> ReassignCustomerPricingAsync(
        Guid sourceCustomerId,
        Guid targetCustomerId,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Pricing customer merge reassignment requires an active command transaction.");
        }

        // Lock pricing.vouchers in ascending id order per Lock Hierarchy DAG.
        var vouchers = await db.Set<Voucher>()
            .FromSqlInterpolated($"SELECT *, xmin FROM pricing.vouchers WHERE bound_customer_id = {sourceCustomerId} ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        foreach (var voucher in vouchers)
        {
            voucher.BoundCustomerId = targetCustomerId;
        }

        var redemptions = await db.Set<VoucherRedemption>()
            .FromSqlInterpolated($"SELECT * FROM pricing.voucher_redemptions WHERE customer_id = {sourceCustomerId} ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        foreach (var redemption in redemptions)
        {
            redemption.CustomerId = targetCustomerId;
        }

        if (vouchers.Length > 0 || redemptions.Length > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return (vouchers.Length, redemptions.Length);
    }
}
