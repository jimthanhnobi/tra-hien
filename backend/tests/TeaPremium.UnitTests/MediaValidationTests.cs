using TeaPremium.Application.Media;

namespace TeaPremium.UnitTests;

public sealed class MediaValidationTests
{
    [Theory]
    [InlineData("image/jpeg", "catalog", 15728640, true)]
    [InlineData("image/png", "content", 15728640, true)]
    [InlineData("image/webp", "branding", 15728640, true)]
    [InlineData("image/avif", "evidence", 15728640, true)]
    [InlineData("video/mp4", "evidence", 52428800, true)]
    [InlineData("video/quicktime", "evidence", 52428800, true)]
    [InlineData("video/mp4", "catalog", 1, false)]
    [InlineData("video/quicktime", "content", 1, false)]
    [InlineData("image/png", "evidence", 15728641, false)]
    [InlineData("video/mp4", "evidence", 52428801, false)]
    [InlineData("image/gif", "content", 1, false)]
    [InlineData("text/html", "catalog", 1, false)]
    [InlineData("image/png", "catalog", 0, false)]
    [InlineData("image/png", "catalog", -1, false)]
    public void D7ExactByteAndMimeBoundaries(string mime, string purpose, long size, bool valid)
        => Assert.Equal(valid, MediaRules.Size(mime, purpose, size));

    [Theory]
    [InlineData("../customer")]
    [InlineData("customer@example.test")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a%2fb")]
    [InlineData("a--b")]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("a\n")]
    public void Nfr04SlugHintRejectsUnsafePath(string hint)
    {
        var result = new CreateUploadValidator().Validate(new CreateUpload("fixture.png", "image/png", 127, "catalog", hint, new(Guid.NewGuid(), [])));
        Assert.False(result.IsValid);
    }
    [Theory]
    [InlineData("catalog", "catalog.write")]
    [InlineData("content", "content.write")]
    [InlineData("branding", "content.write")]
    [InlineData("evidence", "returns.write")]
    public void PurposeRequiresItsOwnPermission(string purpose, string permission)
    {
        Assert.True(new MediaActor(Guid.NewGuid(), [permission]).CanWrite(purpose));
        Assert.False(new MediaActor(Guid.NewGuid(), ["catalog.read"]).CanWrite(purpose));
    }
}
