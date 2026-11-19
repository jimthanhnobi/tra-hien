using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Media;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Media;

public sealed class EfMediaWorkflow(AppDbContext db, IMediaStorage storage, IMediaContentValidator validator, IClock clock) : IMediaWorkflow
{
    public async Task<Result<UploadIntent>> CreateAsync(CreateUpload command, CancellationToken ct)
    {
        if (!command.Actor.CanWrite(command.Purpose)) return Denied<UploadIntent>();
        var id = Guid.CreateVersion7();
        var now = clock.UtcNow;
        // BR-SEO-82. Evidence never uses client hints or names; all other fallbacks are non-PII.
        var slug = command.Purpose == "evidence" ? "evidence" : command.SlugHint ?? command.Purpose;
        var suffix = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6);
        var extension = command.MimeType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            "image/avif" => "avif",
            "video/mp4" => "mp4",
            "video/quicktime" => "mov",
            _ => throw new InvalidOperationException("Unvalidated media command.")
        };
        var key = $"{command.Purpose}/{slug}/{slug}-{suffix}.{extension}";
        // Reserve the final key uniquely in the existing upload_sessions.storage_key constraint.
        var session = new UploadSession
        {
            Id = id,
            StorageKey = key,
            OriginalName = command.FileName,
            MimeType = command.MimeType,
            ExpectedBytes = command.ByteSize,
            Purpose = command.Purpose,
            Status = "pending",
            CreatedBy = command.Actor.Id,
            CreatedAt = now.UtcDateTime,
            ExpiresAt = now.AddMinutes(10).UtcDateTime
        };
        db.Set<UploadSession>().Add(session);
        await db.SaveChangesAsync(ct);
        var url = await storage.PresignPutAsync(MediaKeys.Staging(session), session.MimeType, session.ExpiresAt, ct);
        return Result.Success(new UploadIntent(id, url, new Dictionary<string, string> { ["Content-Type"] = session.MimeType }, session.ExpiresAt));
    }

    public async Task<Result<AssetView>> CompleteAsync(CompleteUpload command, CancellationToken ct)
    {
        // Command decorator owns transaction. Lock before any read/IO, serializing retries and cleanup.
        var session = await db.Set<UploadSession>().FromSqlInterpolated($"SELECT * FROM media.upload_sessions WHERE id={command.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (session is null)
        {
            // Successfully cleaned completed sessions still have an immutable asset with the same ID.
            var existing = await db.Set<MediaAsset>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.Id, ct);
            if (existing is null) return Missing<AssetView>();
            if (existing.CreatedBy != command.Actor.Id || !command.Actor.CanWrite(existing.Purpose)) return Denied<AssetView>();
            return Result.Success(await ViewAsync(existing, ct));
        }
        if (session.CreatedBy != command.Actor.Id || !command.Actor.CanWrite(session.Purpose)) return Denied<AssetView>();
        if (session.Status == "completed")
        {
            var existing = await db.Set<MediaAsset>().SingleAsync(x => x.Id == session.AssetId, ct);
            return Result.Success(await ViewAsync(existing, ct));
        }
        if (session.Status != "pending" || clock.UtcNow.UtcDateTime >= session.ExpiresAt)
            return Rejected<AssetView>("upload_expired");
        var content = await storage.ReadStagingAsync(MediaKeys.Staging(session), session.ExpectedBytes, session.MimeType, ct);
        if (content is null) return Missing<AssetView>();
        if (content.Bytes.LongLength != session.ExpectedBytes || content.MimeType != session.MimeType)
            return Rejected<AssetView>("upload_content_mismatch");
        var dimensions = await validator.ValidateAsync(content, ct);
        if (dimensions is null) return Rejected<AssetView>("upload_content_invalid");
        // Use exactly the bytes we validated, never a server-side copy of a mutable client object.
        await storage.WriteAssetAsync(session.StorageKey, session.Purpose, content, ct);
        var asset = new MediaAsset
        {
            Id = session.Id,
            StorageKey = session.StorageKey,
            MimeType = session.MimeType,
            ByteSize = session.ExpectedBytes,
            Width = dimensions.Width,
            Height = dimensions.Height,
            ChecksumSha256 = Convert.ToHexStringLower(SHA256.HashData(content.Bytes)),
            Purpose = session.Purpose,
            CreatedBy = session.CreatedBy,
            CreatedAt = clock.UtcNow.UtcDateTime
        };
        db.Set<MediaAsset>().Add(asset);
        session.Status = "completed"; session.CompletedAt = clock.UtcNow.UtcDateTime; session.AssetId = asset.Id;
        // If DB fails, staging + the unreferenced deterministic final key remain recoverable by retry/cleanup.
        await db.SaveChangesAsync(ct);
        await db.Entry(asset).ReloadAsync(ct); // PostgreSQL canonical microsecond precision on first and retry responses.
        return Result.Success(await ViewAsync(asset, ct));
    }

    public async Task<Result<MediaPage>> ListAsync(ListMedia query, CancellationToken ct)
    {
        if (!query.Actor.Has("catalog.read")) return Denied<MediaPage>();
        if (query.Purpose == "evidence" && !query.Actor.Has("returns.write")) return Denied<MediaPage>();
        var assets = db.Set<MediaAsset>().AsNoTracking().Where(x => x.Purpose != "evidence"
            || query.Actor.Has("returns.write") && x.CreatedBy == query.Actor.Id);
        if (query.Purpose is not null) assets = assets.Where(x => x.Purpose == query.Purpose);
        // Search public ALT/key only, never the original upload filename.
        if (!string.IsNullOrEmpty(query.Q)) assets = assets.Where(x => x.DefaultAlt != null && x.DefaultAlt.Contains(query.Q)
            || x.Purpose != "evidence" && x.StorageKey.Contains(query.Q));
        var total = await assets.LongCountAsync(ct);
        var rows = await assets.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToListAsync(ct);
        var views = new List<AssetView>();
        foreach (var row in rows) views.Add(await ViewAsync(row, ct));
        return Result.Success(new MediaPage(views, query.Page, query.PageSize, total));
    }

    private async Task<AssetView> ViewAsync(MediaAsset asset, CancellationToken ct)
    {
        var expiry = clock.UtcNow.AddMinutes(10);
        return new(asset.Id, asset.MimeType, asset.ByteSize, asset.Width, asset.Height, asset.ChecksumSha256,
            asset.DefaultAlt, asset.Purpose, asset.CreatedAt, await storage.AssetUrlAsync(asset.StorageKey, asset.Purpose, expiry, ct),
            asset.Purpose == "evidence" ? expiry : null);
    }
    private static Result<T> Denied<T>() => Result.Failure<T>(new("forbidden", "Không có quyền truy cập media.", ErrorType.Forbidden));
    private static Result<T> Missing<T>() => Result.Failure<T>(new("media_not_found", "Không tìm thấy media.", ErrorType.NotFound));
    private static Result<T> Rejected<T>(string code) => Result.Failure<T>(Error.BusinessRule(code, "Upload hết hạn hoặc nội dung không hợp lệ."));
}

public static class MediaKeys
{
    public static string Staging(UploadSession session) => $"pending/{session.Id:N}/{session.StorageKey}";
}
