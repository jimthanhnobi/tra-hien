using TeaPremium.Application.Results;

namespace TeaPremium.Application.Crm.Contracts;

public sealed record ResolvedOrderCustomer(
    Guid CustomerId,
    string CustomerCode,
    string FullName,
    string? PhoneE164,
    string? Email,
    string AcquisitionSource,
    bool HadLegacyPurchase,
    int DeliveredOrdersCount,
    bool IsNewlyCreated);

// 02-architecture.md §3: cross-module port published by CRM so Orders never reads/writes DbSet<Customer> directly.
public interface ICustomerOrderResolver
{
    Task<Result<ResolvedOrderCustomer>> ResolveExistingAsync(Guid customerId, CancellationToken ct);
    Task<Result<ResolvedOrderCustomer>> CreateQuickAdminCustomerAsync(string fullName, string phone, Guid staffId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> ResolveCustomerCodesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct);
}
