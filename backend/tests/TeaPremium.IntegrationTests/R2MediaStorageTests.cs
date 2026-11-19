using System.Net;
using System.Text;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TeaPremium.Application.Media;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Media;

namespace TeaPremium.IntegrationTests;

// SDK protocol tests with a deterministic stub. These are NOT live R2 verification.
public sealed class R2MediaStorageTests
{
    private static MediaStorageOptions Options() => new()
    {
        Provider = "R2",
        Environment = "Development",
        Endpoint = "https://00000000000000000000000000000000.r2.cloudflarestorage.com",
        PublicBaseUrl = "https://media.example.test",
        Public = new() { Bucket = "fixture-dev-public", AccessKeyId = "PUBLIC-FIXTURE", SecretAccessKey = "public-fixture-secret" },
        Private = new() { Bucket = "fixture-dev-private", AccessKeyId = "PRIVATE-FIXTURE", SecretAccessKey = "private-fixture-secret" }
    };
    [Fact]
    public async Task PresignedPutUsesPrivateBucketAndSignedMimeWith600SecondExpiryPrivateGetNeverUsesPublicOrigin()
    {
        var options = Options(); options.Validate(false);
        using var storage = new R2MediaStorage(options);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        var put = new Uri(await storage.PresignPutAsync("pending/fixture/catalog/pot/pot-abc123.png", "image/png", expiry, default));
        Assert.Equal("00000000000000000000000000000000.r2.cloudflarestorage.com", put.Host);
        Assert.StartsWith("/fixture-dev-private/pending/", put.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=600", put.Query, StringComparison.Ordinal);
        Assert.Contains("content-type", Uri.UnescapeDataString(put.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("private-fixture-secret", put.Query, StringComparison.Ordinal);
        var get = new Uri(await storage.AssetUrlAsync("evidence/evidence/evidence-abc123.png", "evidence", expiry, default));
        Assert.StartsWith("/fixture-dev-private/evidence/", get.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=600", get.Query, StringComparison.Ordinal);
        Assert.Equal("https://media.example.test/catalog/pot/pot-abc123.png", await storage.AssetUrlAsync("catalog/pot/pot-abc123.png", "catalog", expiry, default));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-size")]
    [InlineData("wrong-mime")]
    [InlineData("short-read")]
    [InlineData("long-read")]
    [InlineData("get-wrong-mime")]
    [InlineData("valid")]
    public async Task HeadAndConditionalBoundedGetMustMatchExpectedObject(string scenario)
    {
        using var client = new StubS3(scenario);
        using var storage = new R2MediaStorage(Options(), client, client);
        if (scenario == "missing") Assert.Null(await storage.ReadStagingAsync("pending/fixture", 3, "image/png", default));
        else if (scenario == "valid")
        {
            var read = await storage.ReadStagingAsync("pending/fixture", 3, "image/png", default);
            Assert.Equal(new byte[] { 1, 2, 3 }, read!.Bytes); Assert.Equal("fixture-etag", client.LastGet!.EtagToMatch);
        }
        else Assert.Equal("upload_content_mismatch", (await Assert.ThrowsAsync<DomainException>(() => storage.ReadStagingAsync("pending/fixture", 3, "image/png", default))).Code);
    }
    [Fact]
    public async Task PromotionUsesExactBytesConditionalPutAndSeparateBucket()
    {
        using var client = new StubS3("valid"); using var storage = new R2MediaStorage(Options(), client, client);
        await storage.WriteAssetAsync("fixture", "catalog", new([1, 2, 3], "image/png"), default);
        Assert.Equal("fixture-dev-public", client.LastPut!.BucketName); Assert.Equal("*", client.LastPut.IfNoneMatch);
        Assert.Equal(new byte[] { 1, 2, 3 }, client.Written);
        await storage.WriteAssetAsync("fixture", "evidence", new([3, 2, 1], "image/png"), default);
        Assert.Equal("fixture-dev-private", client.LastPut.BucketName); Assert.Equal("private, no-store", client.LastPut.Headers.CacheControl);
    }
    [Fact]
    public async Task ProviderErrorsCannotEscapeWithSignedUrlOrSecretInException()
    {
        using var client = new StubS3("failure"); using var storage = new R2MediaStorage(Options(), client, client);
        var ex = await Assert.ThrowsAsync<MediaStorageException>(() => storage.ReadStagingAsync("fixture", 3, "image/png", default));
        Assert.Null(ex.InnerException); Assert.DoesNotContain("SENSITIVE", ex.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task ConditionalPutRetryAcceptsIdenticalChecksumAndRefusesOverwrite()
    {
        using var same = new StubS3("same-final"); using var retry = new R2MediaStorage(Options(), same, same);
        await retry.WriteAssetAsync("fixture", "catalog", new([1, 2, 3], "image/png"), default);
        using var different = new StubS3("different-final"); using var conflict = new R2MediaStorage(Options(), different, different);
        Assert.Equal("upload_key_conflict", (await Assert.ThrowsAsync<DomainException>(() => conflict.WriteAssetAsync("fixture", "catalog", new([1, 2, 3], "image/png"), default))).Code);
    }
    [Fact]
    public async Task ProviderTimeoutBecomes503BoundaryButCallerCancellationIsPreserved()
    {
        using var client = new StubS3("timeout"); using var storage = new R2MediaStorage(Options(), client, client);
        await Assert.ThrowsAsync<MediaStorageException>(() => storage.ReadStagingAsync("fixture", 3, "image/png", default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => storage.ReadStagingAsync("fixture", 3, "image/png", cancellation.Token));
    }
    [Theory]
    [InlineData("environment")]
    [InlineData("shared-bucket")]
    [InlineData("shared-key")]
    [InlineData("endpoint")]
    [InlineData("secret")]
    public void ConfigurationRejectsUnsafeOrMixedEnvironmentSettings(string kind)
    {
        var options = Options();
        switch (kind)
        {
            case "environment": options.Environment = "Production"; break;
            case "shared-bucket": options.Public.Bucket = options.Private.Bucket; break;
            case "shared-key": options.Public.AccessKeyId = options.Private.AccessKeyId; break;
            case "endpoint": options.Endpoint = "http://localhost/"; break;
            case "secret": options.Private.SecretAccessKey = ""; break;
        }
        Assert.Throws<InvalidOperationException>(() => options.Validate(false));
    }
    private sealed class StubS3(string scenario) : AmazonS3Client(new BasicAWSCredentials("fixture", "fixture"), new AmazonS3Config { ServiceURL = "https://example.test", AuthenticationRegion = "auto" })
    {
        public GetObjectRequest? LastGet { get; private set; }
        public PutObjectRequest? LastPut { get; private set; }
        public byte[]? Written { get; private set; }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            if (scenario == "failure") throw new AmazonS3Exception("https://SENSITIVE?X-Amz-Signature=SENSITIVE-secret");
            if (scenario == "timeout") throw new OperationCanceledException(cancellationToken);
            if (scenario == "missing") throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
            var response = new GetObjectMetadataResponse
            {
                ContentLength = scenario == "wrong-size" ? 4 : 3,
                ETag = "fixture-etag",
                Headers = { ContentType = scenario == "wrong-mime" ? "text/html" : "image/png" }
            };
            response.Metadata["x-amz-meta-sha256"] = scenario == "same-final" ? Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })) : "different";
            return Task.FromResult(response);
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            LastGet = request;
            byte[] bytes = scenario switch { "short-read" => [1], "long-read" => [1, 2, 3, 4], _ => [1, 2, 3] };
            return Task.FromResult(new GetObjectResponse { ContentLength = 3, Headers = { ContentType = scenario == "get-wrong-mime" ? "text/html" : "image/png" }, ResponseStream = new MemoryStream(bytes) });
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            LastPut = request; using var bytes = new MemoryStream(); await request.InputStream.CopyToAsync(bytes, cancellationToken); Written = bytes.ToArray();
            if (scenario is "same-final" or "different-final") throw new AmazonS3Exception("exists") { StatusCode = HttpStatusCode.PreconditionFailed };
            return new PutObjectResponse();
        }
    }
}
