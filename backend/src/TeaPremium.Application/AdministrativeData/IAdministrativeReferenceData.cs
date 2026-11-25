namespace TeaPremium.Application.AdministrativeData;

public sealed record AdministrativeLocation(string ProvinceName, string? WardName, bool IsLocationActive);

// CRM/fulfillment callers must revalidate when reusing an address. This port never
// rewrites a customer's address or an order's historical snapshot.
public interface IAdministrativeReferenceData
{
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<bool> IsActiveSelectionAsync(string provinceCode, string? wardCode, CancellationToken ct);
    Task<IReadOnlyDictionary<(string ProvinceCode, string? WardCode), AdministrativeLocation>> ResolveLocationsAsync(
        IReadOnlyCollection<(string ProvinceCode, string? WardCode)> keys, CancellationToken ct);
}
