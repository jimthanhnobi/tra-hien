using System.Collections.Concurrent;
using TeaPremium.Application.Media;
using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Media;

// Deterministic local/CI adapter, explicitly selected; never registered in Production.
// No HTTP upload route: tests/dev tooling populate staging through this adapter.
public sealed class FakeMediaStorage : IMediaStorage
{
    public ConcurrentDictionary<string, StoredMedia> Staging { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, StoredMedia> Assets { get; } = new(StringComparer.Ordinal);
    public bool Fail { get; set; }
    private void Available() { if (Fail) throw new MediaStorageException(); }
    public Task<string> PresignPutAsync(string stagingKey, string mimeType, DateTimeOffset expiresAt, CancellationToken ct)
    { Available(); return Task.FromResult($"https://private.fake.invalid/{stagingKey}?method=PUT&expires={expiresAt.ToUnixTimeSeconds()}"); }
    public Task<StoredMedia?> ReadStagingAsync(string key, long expectedBytes, string mimeType, CancellationToken ct)
    {
        Available(); Staging.TryGetValue(key, out var item);
        return Task.FromResult(item is null ? null : new StoredMedia((byte[])item.Bytes.Clone(), item.MimeType));
    }
    public Task WriteAssetAsync(string key, string purpose, StoredMedia content, CancellationToken ct)
    {
        Available(); var target = purpose + ":" + key;
        var actual = Assets.GetOrAdd(target, _ => new((byte[])content.Bytes.Clone(), content.MimeType));
        if (actual.MimeType != content.MimeType || !actual.Bytes.AsSpan().SequenceEqual(content.Bytes))
            throw new DomainException("upload_key_conflict", "Media key conflict.");
        return Task.CompletedTask;
    }
    public Task<string> AssetUrlAsync(string key, string purpose, DateTimeOffset expiresAt, CancellationToken ct)
    {
        Available(); return Task.FromResult(purpose == "evidence" ? $"https://private.fake.invalid/{key}?method=GET&expires={expiresAt.ToUnixTimeSeconds()}"
            : $"https://public.fake.invalid/{key}");
    }
    public Task DeleteStagingAsync(string key, CancellationToken ct) { Available(); Staging.TryRemove(key, out _); return Task.CompletedTask; }
    public Task DeleteAssetAsync(string key, string purpose, CancellationToken ct) { Available(); Assets.TryRemove(purpose + ":" + key, out _); return Task.CompletedTask; }
}
