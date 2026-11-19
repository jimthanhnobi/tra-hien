namespace TeaPremium.Application.Media;

public sealed record StoredMedia(byte[] Bytes, string MimeType);
public sealed record MediaDimensions(int? Width, int? Height);
// Every upload lands in private staging; final keys are never authorized for client PUT.
public interface IMediaStorage
{
    Task<string> PresignPutAsync(string stagingKey, string mimeType, DateTimeOffset expiresAt, CancellationToken ct);
    Task<StoredMedia?> ReadStagingAsync(string key, long expectedBytes, string mimeType, CancellationToken ct);
    Task WriteAssetAsync(string key, string purpose, StoredMedia content, CancellationToken ct);
    Task<string> AssetUrlAsync(string key, string purpose, DateTimeOffset expiresAt, CancellationToken ct);
    Task DeleteStagingAsync(string key, CancellationToken ct);
    Task DeleteAssetAsync(string key, string purpose, CancellationToken ct);
}
public interface IMediaContentValidator
{
    Task<MediaDimensions?> ValidateAsync(StoredMedia content, CancellationToken ct);
}
public sealed class MediaStorageException : Exception
{
    public MediaStorageException() : base("Media storage is unavailable.") { }
}
