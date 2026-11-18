namespace TeaPremium.Application.AdministrativeData;

// CRM/fulfillment callers must revalidate when reusing an address. This port never
// rewrites a customer's address or an order's historical snapshot.
public interface IAdministrativeReferenceData
{
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<bool> IsActiveSelectionAsync(string provinceCode, string? wardCode, CancellationToken ct);
}
