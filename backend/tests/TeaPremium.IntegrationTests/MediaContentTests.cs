using TeaPremium.Application.Media;
using TeaPremium.Infrastructure.Media;

namespace TeaPremium.IntegrationTests;

public sealed class MediaContentTests
{
    public static readonly TheoryData<string, string> Formats = new()
    {
        { "jpg", "image/jpeg" }, { "png", "image/png" }, { "webp", "image/webp" },
        { "avif", "image/avif" }, { "mp4", "video/mp4" }, { "mov", "video/quicktime" }
    };
    public static string Ffmpeg => Environment.GetEnvironmentVariable("MediaStorage__FfmpegPath") ?? "ffmpeg";
    public static string Ffprobe => Environment.GetEnvironmentVariable("MediaStorage__FfprobePath") ?? "ffprobe";
    public static byte[] Bytes(string extension) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "media", "valid." + extension));
    private static FfmpegContentValidator Validator() => new(new() { FfmpegPath = Ffmpeg, FfprobePath = Ffprobe });
    [Theory, MemberData(nameof(Formats))]
    public async Task FullDecodeAcceptsAllSixRequiredFormats(string extension, string mime)
    {
        var result = await Validator().ValidateAsync(new(Bytes(extension), mime), default);
        Assert.NotNull(result);
        Assert.Equal(mime.StartsWith("image/", StringComparison.Ordinal) ? 32 : (int?)null, result.Width);
        Assert.Equal(mime.StartsWith("image/", StringComparison.Ordinal) ? 24 : (int?)null, result.Height);
    }
    [Theory, MemberData(nameof(Formats))]
    public async Task TruncatedContentIsRejected(string extension, string mime)
    {
        var bytes = Bytes(extension);
        Assert.Null(await Validator().ValidateAsync(new(bytes[..(bytes.Length / 2)], mime), default));
    }
    [Theory, MemberData(nameof(Formats))]
    public async Task SpoofedContentTypeIsRejected(string extension, string mime)
    {
        var payload = extension == "png" ? Bytes("jpg") : Bytes("png");
        Assert.Null(await Validator().ValidateAsync(new(payload, mime), default));
    }
    [Fact]
    public async Task ValidPngSignatureAndTrailerWithBrokenPixelDataIsRejected()
    {
        var bytes = Bytes("png"); Array.Clear(bytes, 45, 16);
        Assert.Null(await Validator().ValidateAsync(new(bytes, "image/png"), default));
    }
    [Fact]
    public async Task MissingDecoderFailsClosed()
        => await Assert.ThrowsAsync<MediaStorageException>(() => new FfmpegContentValidator(new() { FfprobePath = "missing-decoder-fixture" })
            .ValidateAsync(new(Bytes("png"), "image/png"), default));
}
