using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-05 / BR-CAT-04/07/08/15, BR-SEO-22/23/34, AC-P1-01/02/03/09/10.
public sealed class ProductPublicationTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task PublishReturnsTheEntireErrorListAndNeverWeakensMetadataValidation()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        using var response = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/publish", new { p.RowVersion });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); var body = await response.Content.ReadAsStringAsync();
        foreach (var code in new[] { "publish_requirements_not_met", "short_description_missing", "description_missing", "primary_category_invalid", "cover_alt_missing", "required_attribute_missing", "variant_price_missing", "seo_title_invalid", "seo_description_invalid" }) Assert.Contains(code, body);
        Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
        p = await Ready(h, p, 149); p = await h.Patch(p, new { seoDescription = "short" });
        using var invalidMetadata = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/publish", new { p.RowVersion });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidMetadata.StatusCode); Assert.Contains("seo_description_invalid", await invalidMetadata.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task ActiveAndInactivePricesRespectTheApprovedDraftExceptionAndActivationRules()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await Ready(h, await h.Create(), 150);
        p = await h.Change(p, "/variants", new { status = "inactive", priceVnd = (long?)null }); var inactive = p.Variants.Single(x => x.Status == "inactive");
        p = await h.Change(p, "/publish", new { }); Assert.Equal("active", p.Status); Assert.NotNull(p.FirstPublishedAt);
        inactive = p.Variants.Single(x => x.Status == "inactive");
        using var activation = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + inactive.Id, new { inactive.RowVersion, status = "active" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activation.StatusCode); Assert.Contains("variant_price_missing", await activation.Content.ReadAsStringAsync());
        var active = p.Variants.Single(x => x.Status == "active");
        using var removePrice = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + active.Id, new { active.RowVersion, priceVnd = (long?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removePrice.StatusCode);
        Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
        using var typeChange = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, productTypeId = h.OtherType }); Assert.Equal(HttpStatusCode.Conflict, typeChange.StatusCode);
    }
    [Fact]
    public async Task DuplicateAxisCombinationsBlockPublishButInactiveValuesDoNot()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await Ready(h, await h.Create(), 150);
        p = await h.Change(p, "/variants", new { priceVnd = 2000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } });
        using var bad = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/publish", new { p.RowVersion });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Contains("variant_axis_duplicate", await bad.Content.ReadAsStringAsync());
        var v = p.Variants[1]; p = await h.Change(p, "/variants/" + v.Id, new { status = "inactive", priceVnd = (long?)null }, HttpMethod.Patch, v.RowVersion);
        p = await h.Change(p, "/publish", new { }); Assert.Equal("active", p.Status);
    }
    [Theory]
    [InlineData(149, true)]
    [InlineData(150, false)]
    [InlineData(151, false)]
    public async Task WordBoundariesAreDerivedAndNeverOverwriteManualNoindex(int words, bool thin)
    {
        await using var h = await ProductHarness.Create(fixture); h.App.Configuration["SITE_INDEXABLE"] = "true";
        var p = await Ready(h, await h.Create(), words); p = await h.Change(p, "/publish", new { });
        Assert.Equal(words, p.DescriptionWordCount); Assert.Equal(thin, p.ThinDescription); Assert.Equal(!thin, p.Indexable);
        Assert.Equal(thin ? "thin_description_noindex" : null, p.Warnings.FirstOrDefault()?.Code); Assert.False(p.RobotsNoindex);
        p = await h.Patch(p, new { robotsNoindex = true, noindexReason = "private@example.test lý do" });
        p = await h.Patch(p, new { descriptionHtml = Words(150) }); Assert.True(p.RobotsNoindex); Assert.False(p.ThinDescription); Assert.False(p.Indexable);
        p = await h.Patch(p, new { robotsNoindex = false, descriptionHtml = Words(149) }); Assert.True(p.ThinDescription); Assert.False(p.Indexable);
        p = await h.Patch(p, new { descriptionHtml = Words(151) }); Assert.True(p.Indexable);
        h.App.Configuration["SITE_INDEXABLE"] = "false"; Assert.False((await h.Get(p.Id)).Indexable);
        await using var db = h.App.OwnerContext(); var logs = await db.AuditLogs.Where(x => x.EntityId == p.Id).ToArrayAsync();
        Assert.Contains(logs, x => x.Changes.Contains("thin_description", StringComparison.Ordinal)); Assert.All(logs, x => Assert.DoesNotContain("private@example.test", x.Changes));
        Assert.True(await db.OutboxMessages.CountAsync(x => x.Type == "StorefrontInvalidate") >= 6);
    }
    [Fact]
    public async Task RenameArchiveRestoreAndReclaimFlattenTheSharedGraphAndKeepHistory()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await Ready(h, await h.Create(), 150);
        p = await h.Patch(p, new { slug = "path-a" });
        await using (var db = h.App.OwnerContext()) Assert.Empty(await db.Set<Redirect>().ToArrayAsync());
        p = await h.Change(p, "/publish", new { }); var history = p.FirstPublishedAt;
        p = await h.Patch(p, new { slug = "path-b" }); p = await h.Patch(p, new { slug = "path-c" });
        await using (var db = h.App.OwnerContext())
        {
            var graph = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
            Assert.Equal("/san-pham/path-c", graph["/san-pham/path-a"]); Assert.Equal("/san-pham/path-c", graph["/san-pham/path-b"]);
        }
        p = await h.Change(p, "/archive", new { }); Assert.Equal("/danh-muc/p105-tea", p.ArchivedRedirectPath); Assert.Equal(history, p.FirstPublishedAt);
        p = await h.Change(p, "/restore-draft", new { }); Assert.Equal("draft", p.Status); Assert.Equal(history, p.FirstPublishedAt); Assert.Null(p.ArchivedRedirectPath);
        p = await h.Patch(p, new { slug = "path-a" }); p = await h.Change(p, "/publish", new { }); Assert.Equal(history, p.FirstPublishedAt);
        await using (var db = h.App.OwnerContext())
        {
            var graph = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath); Assert.False(graph.ContainsKey("/san-pham/path-a"));
            Assert.All(graph, pair => Assert.False(graph.ContainsKey(pair.Value)));
        }
    }
    [Fact]
    public async Task NeverPublicArchiveAndUnverifiedPublicHistoryAreNotInvented()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        p = await h.Change(p, "/archive", new { }); Assert.Null(p.FirstPublishedAt); Assert.Null(p.ArchivedRedirectPath);
        await using (var db = h.App.OwnerContext()) Assert.Empty(await db.Set<Redirect>().ToArrayAsync());
        await h.App.ExecuteAsync($"UPDATE catalog.products SET status='active',published_at=now(),first_published_at=NULL WHERE id='{p.Id}'"); p = await h.Get(p.Id);
        using var bad = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/archive", new { p.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode); Assert.Contains("publication_history_unverified", await bad.Content.ReadAsStringAsync());
    }
    internal static string Words(int count) => "<p>" + string.Join("&nbsp;", Enumerable.Repeat("từ", count)) + "</p><script>not counted words</script><style>not counted words</style>";
    internal static async Task<ProductData> Ready(ProductHarness h, ProductData p, int words)
    {
        var v = p.Variants[0]; p = await h.Change(p, "/variants/" + v.Id, new { priceVnd = 1000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } }, HttpMethod.Patch, v.RowVersion);
        p = await h.Patch(p, new { shortDescription = "Mô tả thật", descriptionHtml = Words(words), seoTitle = "Ấm trà", seoDescription = new string('a', 100), categoryIds = new[] { h.Category }, primaryCategoryId = h.Category });
        var asset = Guid.CreateVersion7(); await h.App.ExecuteAsync($"INSERT INTO media.assets(id,storage_key,mime_type,byte_size,purpose) VALUES ('{asset}','catalog/{asset}','image/png',1,'catalog')");
        p = await h.Change(p, "/media", new { assetId = asset, altText = "Ấm trà" }); p = await h.Change(p, "/media/" + p.Media[0].Id + "/cover", new { }); return p;
    }
}
