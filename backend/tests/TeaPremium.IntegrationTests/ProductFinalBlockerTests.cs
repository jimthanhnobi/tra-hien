using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-05 F4/F5: AC-P1-07, BR-CAT-10, BR-SEO-01/03/22; owner-approved scoped semantics.
public sealed class ProductFinalBlockerTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task SearchMatchesEveryLiteralNormalizedTokenOnTheSameProduct()
    {
        await using var h = await ProductHarness.Create(fixture);
        var artisan = await Destination(h, "artisan", "published", "chu-ban", "Chu Bân");
        var p = await h.Create("Ấm tử sa"); p = await h.Patch(p, new { artisanId = artisan });
        await h.Create(initial: new { name = "Chén trà khác", productTypeId = h.OtherType });
        foreach (var q in new[] { "ấm tử sa", "am tu sa", "chu ban", "am tu sa chu ban", "chu ban tu sa", "CHu BÂN ẤM TỬ Sa", " \tấm\u2003tử\u00a0sa\r\n chu   bân ", p.SkuBase })
        {
            var page = await Search(h, q); Assert.Equal(1, page.TotalItems); Assert.Equal(p.Id, Assert.Single(page.Items).Id);
        }
        foreach (var q in new[] { "am chu missing", "am khac", "chu ban chen", "' OR 1=1 --" }) Assert.Empty((await Search(h, q)).Items);
        var literal = await h.Create("Mẫu %_\\ riêng");
        foreach (var q in new[] { "%", "_", "\\", "%_\\", "RIÊNG %" })
        { var page = await Search(h, q); Assert.Equal(literal.Id, Assert.Single(page.Items).Id); Assert.Equal(1, page.TotalItems); }
    }

    [Fact]
    public async Task SearchKeepsEmptyQueriesFiltersPagingTotalsAndValidationBoundaries()
    {
        await using var h = await ProductHarness.Create(fixture);
        var artisan = await Destination(h, "artisan", "published", "chu-ban", "Chu Bân");
        var first = await h.Create("Ấm tử sa"); first = await h.Patch(first, new { artisanId = artisan, categoryIds = new[] { h.Category } });
        var second = await h.Create("Ấm tử sa thứ hai"); await h.Create(initial: new { name = "Chén trà", productTypeId = h.OtherType });
        foreach (var q in new[] { "", " \t\u2003\u00a0\r\n " }) Assert.Equal(3, (await Search(h, q)).TotalItems);
        using (var absent = await h.App.Client.GetAsync(ProductApiTests.Root)) Assert.Equal(3, (await absent.Content.ReadFromJsonAsync<CatalogPage<ProductData>>())!.TotalItems);
        foreach (var filter in new[] { "type=" + h.Type, "material=" + h.Material, "status=draft", "stock=out" }) Assert.Equal(2, (await Search(h, "tu am", "&" + filter)).TotalItems);
        foreach (var filter in new[] { "category=" + h.Category, "artisan=" + artisan }) Assert.Equal(first.Id, Assert.Single((await Search(h, "sa am", "&" + filter)).Items).Id);
        Assert.Empty((await Search(h, "am", "&status=active")).Items);
        Assert.Empty((await Search(h, "am", "&stock=in")).Items);
        var page1 = await Search(h, "tu am", "&page=1&pageSize=1"); var page2 = await Search(h, "tu am", "&page=2&pageSize=1");
        Assert.Equal(2, page1.TotalItems); Assert.Equal(2, page2.TotalItems);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), page1.Items.Concat(page2.Items).Select(x => x.Id).Order());
        Assert.Equal(page1.Items[0].Id, (await Search(h, "am tu", "&page=1&pageSize=1")).Items[0].Id);
        var emptyPage = await Search(h, "am tu", "&page=3&pageSize=1"); Assert.Equal(2, emptyPage.TotalItems); Assert.Empty(emptyPage.Items);
        Assert.Equal(3, (await Search(h, new string(' ', 200))).TotalItems);
        foreach (var suffix in new[] { "?q=" + new string('x', 201), "?q=%00", "?page=0", "?pageSize=101", "?status=wrong", "?stock=wrong" })
        { using var bad = await h.App.Client.GetAsync(ProductApiTests.Root + suffix); await ProductG4FindingTests.Problem(bad, HttpStatusCode.BadRequest, "validation_failed"); }
    }

    [Theory]
    [InlineData("product", "active", false)]
    [InlineData("category", "visible", false)]
    [InlineData("collection", "visible", false)]
    [InlineData("material", "published", false)]
    [InlineData("artisan", "published", false)]
    [InlineData("product", "active", true)]
    [InlineData("category", "visible", true)]
    [InlineData("collection", "visible", true)]
    [InlineData("material", "published", true)]
    [InlineData("artisan", "published", true)]
    public async Task ArchiveAcceptsOnlySupportedPublicFinalDestinationsAndFlattensChains(string kind, string status, bool chain)
    {
        await using var h = await ProductHarness.Create(fixture);
        await Destination(h, kind, status); var target = Prefix(kind) + "target";
        await h.App.ExecuteAsync($"INSERT INTO content.redirects(source_path,target_path) VALUES ('/san-pham/old-a','/danh-muc/old-b'),('/danh-muc/old-b','{target}')");
        var p = await Published(h); var history = p.FirstPublishedAt;
        p = await h.Change(p, "/archive", new { redirectTo = chain ? "/san-pham/old-a" : target });
        Assert.Equal("archived", p.Status); Assert.Equal(history, p.FirstPublishedAt); Assert.Equal(target, p.ArchivedRedirectPath);
        await using var db = h.App.OwnerContext(); var graph = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
        Assert.Equal(target, graph["/san-pham/" + p.Slug]); Assert.False(graph.ContainsKey(target));
    }

    [Theory]
    [InlineData("product", "draft")]
    [InlineData("product", "archived")]
    [InlineData("category", "hidden")]
    [InlineData("collection", "hidden")]
    [InlineData("material", "draft")]
    [InlineData("material", "archived")]
    [InlineData("artisan", "draft")]
    [InlineData("artisan", "archived")]
    public async Task ArchiveRejectsNonpublicFinalEntitiesAtomically(string kind, string status)
    {
        await using var h = await ProductHarness.Create(fixture); await Destination(h, kind, status);
        var p = await Published(h); await Reject(h, p, Prefix(kind) + "target", HttpStatusCode.BadRequest, "validation_failed");
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.BadRequest, "validation_failed")]
    [InlineData("hidden", HttpStatusCode.BadRequest, "validation_failed")]
    [InlineData("unsafe", HttpStatusCode.BadRequest, "validation_failed")]
    [InlineData("self", HttpStatusCode.Conflict, "redirect_conflict")]
    [InlineData("direct-self", HttpStatusCode.Conflict, "redirect_conflict")]
    [InlineData("cycle", HttpStatusCode.Conflict, "redirect_conflict")]
    public async Task ArchiveValidatesTheResolvedLeafNotJustTheInput(string scenario, HttpStatusCode code, string error)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await Published(h);
        if (scenario == "direct-self") { await Reject(h, p, "/san-pham/" + p.Slug, code, error); return; }
        var leaf = scenario switch { "hidden" => "/danh-muc/target", "unsafe" => "/san-pham/%2ftarget", "self" => "/san-pham/" + p.Slug, "cycle" => "/san-pham/old-2", _ => "/san-pham/missing" };
        if (scenario == "hidden") await Destination(h, "category", "hidden");
        await h.App.ExecuteAsync($"INSERT INTO content.redirects(source_path,target_path) VALUES ('/san-pham/old','{leaf}')");
        if (scenario == "cycle") await h.App.ExecuteAsync("INSERT INTO content.redirects(source_path,target_path) VALUES ('/san-pham/old-2','/san-pham/old')");
        await Reject(h, p, "/san-pham/old", code, error);
    }

    [Theory]
    [InlineData("artisan")]
    [InlineData("material")]
    [InlineData("category")]
    [InlineData("none")]
    public async Task OmittedTargetKeepsThePublicFallbackPriority(string best)
    {
        await using var h = await ProductHarness.Create(fixture); var artisan = await Destination(h, "artisan", best == "artisan" ? "published" : "draft");
        await h.App.ExecuteAsync($"UPDATE catalog.materials SET status='{(best is "artisan" or "material" ? "published" : "draft")}',first_published_at=now() WHERE id='{h.Material}'");
        var p = await Published(h); p = await h.Patch(p, new { artisanId = artisan });
        if (best == "none") await h.App.ExecuteAsync($"UPDATE catalog.categories SET status='hidden' WHERE id='{h.Category}'");
        if (best == "none") { await Reject(h, p, null, HttpStatusCode.BadRequest, "validation_failed"); return; }
        p = await h.Change(p, "/archive", new { });
        Assert.Equal(best switch { "artisan" => "/nghe-nhan/target", "material" => "/chat-lieu/p105-clay", _ => "/danh-muc/p105-tea" }, p.ArchivedRedirectPath);
    }

    [Fact]
    public async Task NeverPublicArchiveKeepsNoRedirectAndCreatesNoPublicationHistory()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); p = await h.Change(p, "/archive", new { });
        Assert.Equal("archived", p.Status); Assert.Null(p.FirstPublishedAt); Assert.Null(p.PublishedAt); Assert.Null(p.ArchivedRedirectPath);
        await using var db = h.App.OwnerContext(); Assert.Empty(await db.Set<Redirect>().ToArrayAsync());
    }

    internal static async Task<ProductData> Published(ProductHarness h)
    { var p = await ProductPublicationTests.Ready(h, await h.Create(), 150); return await h.Change(p, "/publish", new { }); }
    internal static string Prefix(string kind) => kind switch { "product" => "/san-pham/", "category" => "/danh-muc/", "collection" => "/bo-suu-tap/", "material" => "/chat-lieu/", _ => "/nghe-nhan/" };
    internal static async Task<Guid> Destination(ProductHarness h, string kind, string status, string slug = "target", string name = "Đích công khai")
    {
        var id = Guid.CreateVersion7();
        var sql = kind switch
        {
            "product" => $"INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,first_published_at) VALUES ('{id}','{h.Type}','TG-XX-9999','{slug}','{name}','{status}',now(),now()); INSERT INTO catalog.variants(id,product_id,sku,price_vnd) VALUES ('{Guid.CreateVersion7()}','{id}','TG-XX-9999',1000)",
            "material" => $"INSERT INTO catalog.materials(id,slug,name,status,kind,first_published_at) VALUES ('{id}','{slug}','{name}','{status}','clay',now())",
            _ => $"INSERT INTO catalog.{(kind == "category" ? "categories" : kind == "collection" ? "collections" : "artisans")}(id,slug,name,status,first_published_at) VALUES ('{id}','{slug}','{name}','{status}',now())"
        };
        await h.App.ExecuteAsync(sql); return id;
    }
    internal static async Task<string> Fingerprint(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        return await ProductG4FindingTests.Fingerprint(h) + JsonSerializer.Serialize(new
        {
            redirects = await db.Set<Redirect>().AsNoTracking().OrderBy(x => x.SourcePath).ToArrayAsync(),
            categories = await db.Set<Category>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            collections = await db.Set<CollectionRecord>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            materials = await db.Set<Material>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            artisans = await db.Set<Artisan>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
    internal static async Task Reject(ProductHarness h, ProductData p, string? target, HttpStatusCode status, string code)
    {
        var before = await Fingerprint(h);
        using var response = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/archive", new { p.RowVersion, redirectTo = target });
        await ProductG4FindingTests.Problem(response, status, code); Assert.Equal(before, await Fingerprint(h));
    }
    private static async Task<CatalogPage<ProductData>> Search(ProductHarness h, string q, string suffix = "")
    {
        using var response = await h.App.Client.GetAsync(ProductApiTests.Root + "?q=" + Uri.EscapeDataString(q) + suffix);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<CatalogPage<ProductData>>())!;
    }
}

// Separate disposable fixture keeps per-class PostgreSQL connection pools bounded.
public sealed class ProductFinalBlockerPathTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Theory]
    [InlineData("/san-pham/missing")]
    [InlineData("/danh-muc/missing")]
    [InlineData("/bo-suu-tap/missing")]
    [InlineData("/chat-lieu/missing")]
    [InlineData("/nghe-nhan/missing")]
    [InlineData("https://evil.example/path")]
    [InlineData("//evil.example/path")]
    [InlineData("/%2f%2fevil.example/path")]
    [InlineData("/san-pham/%5ctarget")]
    [InlineData("/san-pham/%252ftarget")]
    [InlineData("/san-pham/%74arget")]
    [InlineData("/san-pham/%00")]
    [InlineData("/san-pham/target?x=1")]
    [InlineData("/san-pham/target#part")]
    [InlineData("/san-pham/target/")]
    [InlineData("/san-pham/Target")]
    [InlineData("/san-pham/../target")]
    [InlineData("/san-pham/tử-sa")]
    [InlineData("/san-pham/target\\other")]
    [InlineData("/kien-thuc/target")]
    [InlineData("/san-pham/target\n")]
    public async Task ArchiveRejectsMissingUnsupportedAndNoncanonicalPaths(string path)
    { await using var h = await ProductHarness.Create(fixture); var p = await ProductFinalBlockerTests.Published(h); await ProductFinalBlockerTests.Reject(h, p, path, HttpStatusCode.BadRequest, "validation_failed"); }

}
