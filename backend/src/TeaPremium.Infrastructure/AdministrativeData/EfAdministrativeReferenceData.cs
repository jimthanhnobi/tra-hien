using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.AdministrativeData;

public sealed class EfAdministrativeReferenceData(AppDbContext db, IConfiguration configuration) : IAdministrativeReferenceData
{
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        var events = await db.AdministrativeDataImports.AsNoTracking().ToArrayAsync(ct);
        var predecessorIds = events.Where(x => x.PreviousImportId.HasValue).Select(x => x.PreviousImportId!.Value).ToHashSet();
        var tails = events.Where(x => !predecessorIds.Contains(x.Id)).ToArray();
        if (tails.Length != 1 || configuration.GetSection("AdministrativeImport:TrustedManifestSha256").Get<string[]>()?
            .Contains(tails[0].ManifestSha256, StringComparer.Ordinal) != true) return false;
        var provinces = await db.Provinces.AsNoTracking().Select(p => new ProvinceData(p.Code, p.Name, p.IsActive, p.SortOrder)).ToArrayAsync(ct);
        var wards = await db.Wards.AsNoTracking().Select(w => new WardData(w.Code, w.ProvinceCode, w.Name, w.IsActive)).ToArrayAsync(ct);
        return provinces.Any(p => p.IsActive) && wards.All(w => !w.IsActive || provinces.Any(p => p.Code == w.ProvinceCode && p.IsActive))
            && new AdministrativeCatalog(provinces, wards).Digest() == tails[0].CatalogSha256;
    }
    public async Task<bool> IsActiveSelectionAsync(string provinceCode, string? wardCode, CancellationToken ct)
        => await IsReadyAsync(ct) && await db.Provinces.AsNoTracking().AnyAsync(p => p.Code == provinceCode && p.IsActive, ct)
            && (wardCode is null || await db.Wards.AsNoTracking().AnyAsync(w => w.Code == wardCode && w.ProvinceCode == provinceCode && w.IsActive, ct));

    public async Task<IReadOnlyDictionary<(string ProvinceCode, string? WardCode), AdministrativeLocation>> ResolveLocationsAsync(
        IReadOnlyCollection<(string ProvinceCode, string? WardCode)> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<(string ProvinceCode, string? WardCode), AdministrativeLocation>();
        }

        var distinctKeys = keys.Distinct().ToArray();
        var provinceCodes = distinctKeys.Select(k => k.ProvinceCode).Distinct(StringComparer.Ordinal).ToArray();
        var wardCodes = distinctKeys.Where(k => k.WardCode is not null).Select(k => k.WardCode!).Distinct(StringComparer.Ordinal).ToArray();

        var provinces = await db.Provinces.AsNoTracking()
            .Where(p => provinceCodes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, ct);

        var wards = wardCodes.Length == 0
            ? new Dictionary<string, Ward>(StringComparer.Ordinal)
            : await db.Wards.AsNoTracking()
                .Where(w => wardCodes.Contains(w.Code))
                .ToDictionaryAsync(w => w.Code, StringComparer.Ordinal, ct);

        var result = new Dictionary<(string ProvinceCode, string? WardCode), AdministrativeLocation>(distinctKeys.Length);
        foreach (var key in distinctKeys)
        {
            var hasProvince = provinces.TryGetValue(key.ProvinceCode, out var province);
            Ward? ward = null;
            var hasWard = key.WardCode is not null && wards.TryGetValue(key.WardCode, out ward);
            var isLocationActive = hasProvince && province!.IsActive
                && (key.WardCode is null
                    || (hasWard && ward!.IsActive && string.Equals(ward.ProvinceCode, key.ProvinceCode, StringComparison.Ordinal)));

            result[key] = new AdministrativeLocation(
                hasProvince ? province!.Name : "",
                hasWard ? ward!.Name : null,
                isLocationActive);
        }

        return result;
    }

    public async Task<IReadOnlyList<AdministrativeProvinceItem>> ListActiveProvincesAsync(CancellationToken ct) =>
        await db.Provinces.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Code)
            .Select(p => new AdministrativeProvinceItem(p.Code, p.Name, p.SortOrder))
            .ToArrayAsync(ct);

    public async Task<StoreWardListView?> ListActiveWardsByProvinceAsync(string provinceCode, CancellationToken ct)
    {
        var province = await db.Provinces.AsNoTracking()
            .Where(p => p.Code == provinceCode && p.IsActive)
            .Select(p => new { p.Code, p.Name })
            .SingleOrDefaultAsync(ct);

        if (province is null) return null;

        var wards = await db.Wards.AsNoTracking()
            .Where(w => w.ProvinceCode == province.Code && w.IsActive)
            .OrderBy(w => w.Name)
            .ThenBy(w => w.Code)
            .Select(w => new AdministrativeWardItem(w.Code, w.ProvinceCode, w.Name))
            .ToArrayAsync(ct);

        return new StoreWardListView(province.Code, province.Name, wards);
    }
}
