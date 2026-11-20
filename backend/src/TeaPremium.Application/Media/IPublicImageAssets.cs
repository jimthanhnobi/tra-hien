namespace TeaPremium.Application.Media;

// Catalog may only reference completed, non-evidence images. No private URL/key crosses this port.
public interface IPublicImageAssets
{
    Task<bool> ExistsAsync(Guid assetId, CancellationToken ct);
    Task<string?> UrlAsync(Guid assetId, CancellationToken ct);
}
