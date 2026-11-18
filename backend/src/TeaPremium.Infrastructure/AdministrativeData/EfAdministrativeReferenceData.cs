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
}
