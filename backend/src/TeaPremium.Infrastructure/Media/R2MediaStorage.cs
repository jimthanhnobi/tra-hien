using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TeaPremium.Application.Media;
using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Media;

public sealed class R2MediaStorage : IMediaStorage, IDisposable
{
    private readonly MediaStorageOptions _options;
    private readonly IAmazonS3 _public;
    private readonly IAmazonS3 _private;
    public R2MediaStorage(MediaStorageOptions options)
    {
        _options = options;
        _public = Client(options.Public); _private = Client(options.Private);
    }
    public R2MediaStorage(MediaStorageOptions options, IAmazonS3 publicClient, IAmazonS3 privateClient)
    {
        _options = options; _public = publicClient; _private = privateClient;
    }
    private AmazonS3Client Client(R2BucketOptions bucket) => new(new BasicAWSCredentials(bucket.AccessKeyId, bucket.SecretAccessKey), new AmazonS3Config
    {
        ServiceURL = _options.Endpoint,
        AuthenticationRegion = "auto",
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromSeconds(30),
        MaxErrorRetry = 2
    });
    public Task<string> PresignPutAsync(string stagingKey, string mimeType, DateTimeOffset expiresAt, CancellationToken ct)
        => Safe(async () => await _private.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Private.Bucket,
            Key = stagingKey,
            Verb = HttpVerb.PUT,
            ContentType = mimeType,
            Expires = expiresAt.UtcDateTime,
            Protocol = Protocol.HTTPS
        }), ct);

    public Task<StoredMedia?> ReadStagingAsync(string key, long expectedBytes, string mimeType, CancellationToken ct) => Safe<StoredMedia?>(async () =>
    {
        GetObjectMetadataResponse head;
        try { head = await _private.GetObjectMetadataAsync(new() { BucketName = _options.Private.Bucket, Key = key }, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        if (head.ContentLength != expectedBytes || head.Headers.ContentType != mimeType) throw InvalidContent();
        // Conditional read binds HEAD to GET even if a signed client PUT races the complete call.
        using var response = await _private.GetObjectAsync(new GetObjectRequest { BucketName = _options.Private.Bucket, Key = key, EtagToMatch = head.ETag }, ct);
        if (response.ContentLength != expectedBytes || response.Headers.ContentType != mimeType) throw InvalidContent();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await response.ResponseStream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > expectedBytes) throw InvalidContent();
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (output.Length != expectedBytes) throw InvalidContent();
        return new(output.ToArray(), response.Headers.ContentType);
    }, ct);

    public Task WriteAssetAsync(string key, string purpose, StoredMedia content, CancellationToken ct) => Safe(async () =>
    {
        var client = purpose == "evidence" ? _private : _public;
        var bucket = purpose == "evidence" ? _options.Private.Bucket : _options.Public.Bucket;
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content.Bytes));
        using var input = new MemoryStream(content.Bytes, writable: false);
        var put = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = content.MimeType,
            InputStream = input,
            IfNoneMatch = "*",
            DisablePayloadSigning = true,
            UseChunkEncoding = false,
            Headers = { CacheControl = purpose == "evidence" ? "private, no-store" : "public, max-age=31536000, immutable" }
        };
        put.Metadata["sha256"] = checksum;
        try { await client.PutObjectAsync(put, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var existing = await client.GetObjectMetadataAsync(new() { BucketName = bucket, Key = key }, ct);
            if (existing.ContentLength != content.Bytes.LongLength || existing.Headers.ContentType != content.MimeType
                || existing.Metadata["x-amz-meta-sha256"] != checksum)
                throw new DomainException("upload_key_conflict", "Media key conflict.");
        }
        return true;
    }, ct);

    public Task<string> AssetUrlAsync(string key, string purpose, DateTimeOffset expiresAt, CancellationToken ct)
        => purpose != "evidence" ? Task.FromResult(_options.PublicBaseUrl.TrimEnd('/') + "/" + key) : Safe(async () =>
            await _private.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = _options.Private.Bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = expiresAt.UtcDateTime,
                Protocol = Protocol.HTTPS,
                ResponseHeaderOverrides = new() { CacheControl = "private, no-store" }
            }), ct);
    public Task DeleteStagingAsync(string key, CancellationToken ct) => DeleteAsync(_private, _options.Private.Bucket, key, ct);
    public Task DeleteAssetAsync(string key, string purpose, CancellationToken ct) => DeleteAsync(purpose == "evidence" ? _private : _public,
        purpose == "evidence" ? _options.Private.Bucket : _options.Public.Bucket, key, ct);
    private static Task<bool> DeleteAsync(IAmazonS3 client, string bucket, string key, CancellationToken ct) => Safe(async () =>
    { await client.DeleteObjectAsync(new() { BucketName = bucket, Key = key }, ct); return true; }, ct);
    private static DomainException InvalidContent() => new("upload_content_mismatch", "Uploaded object does not match intent.");
    private static async Task<T> Safe<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DomainException) { throw; }
        catch (Exception) { throw new MediaStorageException(); } // SDK errors can embed signed URLs/credentials.
    }
    public void Dispose() { _public.Dispose(); _private.Dispose(); }
}
