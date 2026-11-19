namespace TeaPremium.Infrastructure.Media;

public sealed class MediaStorageOptions
{
    public string Provider { get; set; } = "Disabled";
    public string Environment { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public R2BucketOptions Public { get; set; } = new();
    public R2BucketOptions Private { get; set; } = new();
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    public void Validate(bool production)
    {
        if (Provider != "R2") throw new InvalidOperationException("R2 configuration required.");
        if (Environment != (production ? "Production" : "Development")) throw new InvalidOperationException("Media environment mismatch.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || !System.Text.RegularExpressions.Regex.IsMatch(endpoint.Host, "\\A[a-f0-9]{32}(?:\\.(?:eu|fedramp))?\\.r2\\.cloudflarestorage\\.com\\z")
            || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0 || !endpoint.IsDefaultPort)
            throw new InvalidOperationException("Invalid R2 endpoint.");
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidOperationException("Invalid media public origin.");
        foreach (var bucket in new[] { Public, Private })
            if (!System.Text.RegularExpressions.Regex.IsMatch(bucket.Bucket, "\\A[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\\z")
                || string.IsNullOrWhiteSpace(bucket.AccessKeyId) || string.IsNullOrWhiteSpace(bucket.SecretAccessKey))
                throw new InvalidOperationException("Bucket-scoped media credentials required.");
        if (Public.Bucket == Private.Bucket || Public.AccessKeyId == Private.AccessKeyId)
            throw new InvalidOperationException("Separate public/private buckets and credentials required.");
        // Provider-side scope/privacy/CORS and dev/prod separation require live operator verification.
    }
}
public sealed class R2BucketOptions
{
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
}
