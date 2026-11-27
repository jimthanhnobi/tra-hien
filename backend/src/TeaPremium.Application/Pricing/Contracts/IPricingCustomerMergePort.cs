namespace TeaPremium.Application.Pricing.Contracts;

// 02 §3 cross-module contract owned by Pricing for P2-12 customer merge reassignment.
public interface IPricingCustomerMergePort
{
    Task<(int MovedVouchers, int MovedRedemptions)> ReassignCustomerPricingAsync(
        Guid sourceCustomerId,
        Guid targetCustomerId,
        CancellationToken ct);
}
