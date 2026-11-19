using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.UnitTests;

public sealed class TaxonomyRulesTests
{
    [Theory]
    [InlineData("Ấm Đất — Tử Nê!", "am-dat-tu-ne")]
    [InlineData("  A___B---C  ", "a-b-c")]
    [InlineData("ĐẬM", "dam")]
    [InlineData("漢字", "")]
    public void BrCat01VietnameseSlug(string name, string expected) => Assert.Equal(expected, TaxonomyRules.Slug(name));
    [Fact]
    public void BrCat01SuffixNeverExceeds80OrLeavesTrailingHyphen()
    {
        var basis = TaxonomyRules.Slug(new string('a', 100)); Assert.Equal(80, basis.Length);
        Assert.Equal(80, TaxonomyRules.Candidate(basis, 1000).Length);
        Assert.EndsWith("-1000", TaxonomyRules.Candidate(basis, 1000), StringComparison.Ordinal);
        Assert.False(TaxonomyRules.ValidSlug("a-")); Assert.False(TaxonomyRules.ValidSlug("A"));
    }
    [Fact]
    public void BrCat09FullSubtreeDepthMissingParentAndCycles()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?> { [a] = null, [b] = a, [c] = b };
        Assert.True(TaxonomyRules.ValidTree(parents));
        parents[d] = c; Assert.False(TaxonomyRules.ValidTree(parents));
        parents[d] = null; parents[a] = d; Assert.False(TaxonomyRules.ValidTree(parents));
        parents[a] = c; Assert.False(TaxonomyRules.ValidTree(parents));
        parents[a] = a; Assert.False(TaxonomyRules.ValidTree(parents));
        parents[a] = Guid.NewGuid(); Assert.False(TaxonomyRules.ValidTree(parents));
    }
    [Fact]
    public void BrCat08FlattenReuseAndCrossPrefixInboundPreserveUnrelated()
    {
        var first = TaxonomyRules.Redirects(new Dictionary<string, string> { ["/unrelated"] = "/other", ["/external"] = "/chat-lieu/a" }, "/chat-lieu/a", "/chat-lieu/b");
        var second = TaxonomyRules.Redirects(first, "/chat-lieu/b", "/chat-lieu/c");
        Assert.Equal("/chat-lieu/c", second["/chat-lieu/a"]); Assert.Equal("/chat-lieu/c", second["/chat-lieu/b"]);
        Assert.Equal("/chat-lieu/c", second["/external"]); Assert.Equal("/other", second["/unrelated"]);
        var reuse = TaxonomyRules.Redirects(second, "/chat-lieu/c", "/chat-lieu/a");
        Assert.False(reuse.ContainsKey("/chat-lieu/a")); Assert.Equal("/chat-lieu/a", reuse["/chat-lieu/c"]);
        Assert.All(reuse, pair => Assert.False(reuse.ContainsKey(pair.Value)));
    }
    [Theory]
    [InlineData(CatalogKind.Material, "Tử nê: đặc điểm, cách chọn và tác phẩm", "Thông tin về chất liệu Tử nê.")]
    [InlineData(CatalogKind.Artisan, "Nghệ nhân Tử nê", "Thông tin về nghệ nhân Tử nê.")]
    public void BrSeo31And32MissingOptionalSourcesAreNeutral(CatalogKind kind, string title, string description)
    {
        var seo = CatalogSeo.Resolve(new CatalogEntry { Name = "Tử nê" }, kind, "", null);
        Assert.Equal(title, seo.Title); Assert.Equal(description, seo.Description);
    }
    [Fact]
    public void BrSeo31And32PrioritiesBrandAndExplicitValuesAreNeverOverwritten()
    {
        var entry = new CatalogEntry { Name = "Chu Bân", Summary = "Summary", SeoTitle = " My custom title ", SeoDescription = "My custom description" };
        var seo = CatalogSeo.Resolve(entry, CatalogKind.Material, "Body", "Brand");
        Assert.Equal(entry.SeoTitle, seo.Title); Assert.Equal(entry.SeoDescription, seo.Description);
        entry.SeoTitle = "\u00a0"; entry.SeoDescription = null;
        seo = CatalogSeo.Resolve(entry, CatalogKind.Material, "Body", null); Assert.Equal("Summary", seo.Description);
        entry.Summary = null; seo = CatalogSeo.Resolve(entry, CatalogKind.Artisan, "Body", "Brand");
        Assert.Equal("Nghệ nhân Chu Bân | Brand", seo.Title); Assert.Equal("Body", seo.Description);
        Assert.Null(entry.SeoDescription); Assert.Equal("\u00a0", entry.SeoTitle);
        Assert.Equal("a b", CatalogSeo.TrimWords("a\u00a0b\u2003cccccc", 4));
        Assert.Equal("longword", CatalogSeo.TrimWords("longword x", 3));
    }
    [Fact]
    public void RowVersionIsOpaqueStrictRoundTrip()
    {
        Assert.True(CatalogVersions.TryDecode(CatalogVersions.Encode(1234), out var actual)); Assert.Equal(1234u, actual);
        Assert.False(CatalogVersions.TryDecode("invalid", out _)); Assert.False(CatalogVersions.TryDecode(null, out _));
    }
}
