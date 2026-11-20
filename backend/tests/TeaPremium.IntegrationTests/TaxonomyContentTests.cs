using System.Text.Json;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Media;

namespace TeaPremium.IntegrationTests;

public sealed class TaxonomyContentTests
{
    private static CatalogContentSanitizer Content() => new CatalogContentSanitizer(new MediaStorageOptions { PublicBaseUrl = "https://media.example.invalid" });
    [Theory]
    [InlineData(null, "", 0)]
    [InlineData("", "", 0)]
    [InlineData("<p>Một&nbsp;từ</p><p>hai &amp; ba</p>", "Một từ hai & ba", 5)]
    [InlineData("<p>một&#160;hai&#x2003;ba</p>", "một hai ba", 3)]
    [InlineData("<script>secret script words</script><style>.x { text: more words }</style><p>thật</p>", "thật", 1)]
    [InlineData("<p>&lt;script&gt;x&lt;/script&gt;</p>", "<script>x</script>", 1)]
    [InlineData("<p>&amp;nbsp;</p>", "&nbsp;", 1)]
    [InlineData("<div>one</div><div>two</div>", "one two", 2)]
    [InlineData("<section>one<div>two<p>three</p>four</div>five</section><div>six</div>", "one two three four five six", 6)]
    [InlineData("<h1>one</h1><h2>two</h2><p>three</p><ul><li>four</li><li>five</li></ul>", "one two three four five", 5)]
    [InlineData("<div>one&nbsp;two</div><section>three&#x2003;four</section><script>secret</script><style>secret</style>", "one two three four", 4)]
    public void BrSeo35VisibleTextDecodesExactlyOnce(string? html, string text, int words)
    {
        var result = Content().Sanitize(null, html); Assert.Equal(text, result.Text); Assert.Equal(words, result.Words);
        var persisted = Content().Sanitize(null, result.Html);
        Assert.Equal(text, persisted.Text); Assert.Equal(words, persisted.Words);
    }
    [Fact]
    public void BrCnt02HtmlParserRemovesExecutableContentPrivateImagesAndUnsafeUrls()
    {
        var result = Content().Sanitize(null, "<p onclick='bad()'>A <strong>B</strong></p><a href='javascript:bad()'>link</a><a href='https://example.invalid'>safe</a>"
            + "<img src='https://private.invalid/evidence.png' onerror='bad()'><img src='https://media.example.invalid/catalog/a.png' alt='safe'>");
        Assert.DoesNotContain("onclick", result.Html, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("javascript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.invalid", result.Html, StringComparison.Ordinal); Assert.DoesNotContain("onerror", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noopener nofollow", result.Html, StringComparison.Ordinal); Assert.Contains("media.example.invalid", result.Html, StringComparison.Ordinal);
    }
    [Fact]
    public void BrCnt02TiptapSourceIsRenderedOnServerAndUntrustedHtmlCannotOverrideIt()
    {
        var json = JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = "A & B" } } } } });
        var result = Content().Sanitize(json, "<script>bad()</script><p>forged</p>");
        Assert.Equal("A & B", result.Text); Assert.Equal(3, result.Words); Assert.DoesNotContain("forged", result.Html, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Content().Sanitize("{\"type\":\"doc\",\"content\":[{\"type\":\"script\"}]}", null));
        Assert.Throws<ArgumentException>(() => Content().Sanitize("{\"type\":\"doc\",\"content\":[{\"type\":\"heading\"}]}", null));
    }
}
