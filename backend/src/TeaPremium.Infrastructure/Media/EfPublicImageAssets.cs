using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Media;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Media;

public sealed class EfPublicImageAssets(AppDbContext db, MediaStorageOptions options) : IPublicImageAssets
{
    private IQueryable<MediaAsset> PublicImages => db.Set<MediaAsset>().Where(x => x.Purpose != "evidence"
        && (x.MimeType == "image/png" || x.MimeType == "image/jpeg" || x.MimeType == "image/webp" || x.MimeType == "image/avif"));
    public Task<bool> ExistsAsync(Guid assetId, CancellationToken ct) => PublicImages.AnyAsync(x => x.Id == assetId, ct);
    public async Task<string?> UrlAsync(Guid assetId, CancellationToken ct)
    {
        var key = await PublicImages.Where(x => x.Id == assetId).Select(x => x.StorageKey).SingleOrDefaultAsync(ct);
        return key is null || !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            ? null : options.PublicBaseUrl.TrimEnd('/') + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    }
}
