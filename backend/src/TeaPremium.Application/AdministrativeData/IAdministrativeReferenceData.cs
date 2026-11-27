namespace TeaPremium.Application.AdministrativeData;

public sealed record AdministrativeLocation(string ProvinceName, string? WardName, bool IsLocationActive);

public sealed record AdministrativeProvinceItem(string Code, string Name, int SortOrder);

public sealed record AdministrativeWardItem(string Code, string ProvinceCode, string Name);

public sealed record StoreProvinceListView(IReadOnlyList<AdministrativeProvinceItem> Items);

public sealed record StoreWardListView(
    string ProvinceCode,
    string ProvinceName,
    IReadOnlyList<AdministrativeWardItem> Items);

public static class AdministrativeProvinceCode
{
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (raw is null) return false;

        foreach (var ch in raw)
        {
            if (ch == '\0' || char.IsControl(ch))
                return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is < 1 or > 16)
            return false;

        foreach (var ch in trimmed)
        {
            var valid = ch is (>= '0' and <= '9')
                or (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or '-'
                or '_';
            if (!valid)
                return false;
        }

        normalized = trimmed;
        return true;
    }
}

// CRM/fulfillment callers must revalidate when reusing an address. This port never
// rewrites a customer's address or an order's historical snapshot.
public interface IAdministrativeReferenceData
{
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<bool> IsActiveSelectionAsync(string provinceCode, string? wardCode, CancellationToken ct);
    Task<IReadOnlyDictionary<(string ProvinceCode, string? WardCode), AdministrativeLocation>> ResolveLocationsAsync(
        IReadOnlyCollection<(string ProvinceCode, string? WardCode)> keys, CancellationToken ct);
    Task<IReadOnlyList<AdministrativeProvinceItem>> ListActiveProvincesAsync(CancellationToken ct);
    Task<StoreWardListView?> ListActiveWardsByProvinceAsync(string provinceCode, CancellationToken ct);
}
