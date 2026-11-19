using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-04 / FR-CAT-02/03/08, BR-CAT-01/06/08/09/10/14, BR-SEO-31/32/35.
// Every harness uses its own PG17 database and real tp_app/AdminCookie/CSRF.
public sealed class TaxonomyApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/";
    private static readonly string[] Resources = ["categories", "collections", "materials", "artisans"];
    private static readonly string[] ConcurrentSlugs = ["same", "same-2"];
    [Theory]
    [InlineData("categories")]
    [InlineData("collections")]
    [InlineData("materials")]
    [InlineData("artisans")]
    public async Task CrudPartialPatchValidationVersionsAndStableIdentity(string resource)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var created = await h.CreateAsync(resource, new { name = "Ấm Đất", kind = "clay", summary = "Keep metadata" });
        Assert.Equal("am-dat", created.Slug); Assert.Null(created.FirstPublishedAt);
        using var get = await h.App.Client.GetAsync(Root + resource + "/" + created.Id); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var list = await h.App.Client.GetAsync(Root + resource + "?page=1&pageSize=1&q=Ấm");
        var page = (await list.Content.ReadFromJsonAsync<CatalogPage>())!; Assert.Single(page.Items); Assert.Equal(1, page.TotalItems);
        var patched = await h.PatchAsync(resource, created, new { name = "New name" });
        Assert.Equal(created.Id, patched.Id); Assert.Equal(created.Slug, patched.Slug); Assert.NotEqual(created.RowVersion, patched.RowVersion);
        if (resource == "materials") Assert.Equal("Keep metadata", patched.Summary);
        using var stale = await h.PatchResponseAsync(resource, created, new { name = "Stale" }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var malformed = await h.App.MutateAsync(HttpMethod.Patch, Root + resource + "/" + created.Id, new { rowVersion = "bad", name = "Bad" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var duplicate = await h.CreateResponseAsync(resource, new { name = "Duplicate", kind = "clay", slug = created.Slug }); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var auto = await h.CreateAsync(resource, new { name = "Ấm Đất", kind = "clay" }); Assert.Equal("am-dat-2", auto.Slug);
        using var invalid = await h.PatchResponseAsync(resource, patched, new { name = "", unknown = "field" }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var crossEntity = await h.App.MutateAsync(HttpMethod.Patch, Root + (resource == "artisans" ? "materials" : "artisans") + "/" + created.Id, new { rowVersion = patched.RowVersion, name = "Wrong entity" });
        Assert.Equal(HttpStatusCode.NotFound, crossEntity.StatusCode);
        using var deletion = await h.App.MutateAsync(HttpMethod.Delete, Root + resource + "/" + created.Id, new { }); Assert.Equal(HttpStatusCode.MethodNotAllowed, deletion.StatusCode);
    }

    [Fact]
    public async Task CategoryDepthCyclesSubtreeOverflowAndConcurrentReparentCannotCorruptTree()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var a = await h.CreateAsync("categories", new { name = "A" });
        var b = await h.CreateAsync("categories", new { name = "B", parentId = a.Id });
        var c = await h.CreateAsync("categories", new { name = "C", parentId = b.Id });
        using (var fourth = await h.CreateResponseAsync("categories", new { name = "Depth four", parentId = c.Id })) Assert.Equal(HttpStatusCode.BadRequest, fourth.StatusCode);
        using (var direct = await h.PatchResponseAsync("categories", a, new { parentId = a.Id })) Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
        using (var cycle = await h.PatchResponseAsync("categories", a, new { parentId = c.Id })) Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
        var d = await h.CreateAsync("categories", new { name = "D" });
        using (var overflow = await h.PatchResponseAsync("categories", a, new { parentId = d.Id })) Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        using (var missing = await h.PatchResponseAsync("categories", a, new { parentId = Guid.NewGuid() })) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var x = await h.CreateAsync("categories", new { name = "X" }); var y = await h.CreateAsync("categories", new { name = "Y" });
        await h.App.ExecuteAsync("""
CREATE FUNCTION catalog.pause_reparent_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(1); RETURN NEW; END $$;
CREATE TRIGGER pause_reparent_fixture BEFORE UPDATE ON catalog.categories FOR EACH ROW EXECUTE FUNCTION catalog.pause_reparent_fixture();
""");
        var first = h.PatchResponseAsync("categories", x, new { parentId = y.Id });
        var sleeping = false;
        for (var i = 0; i < 100 && !(sleeping = await fixture.ObserveSleepingAsync(h.App.Database)); i++) await Task.Delay(10);
        Assert.True(sleeping, "First transaction must hold the hierarchy lock while the competing reparent starts.");
        var concurrent = await Task.WhenAll(first, h.PatchResponseAsync("categories", y, new { parentId = x.Id }));
        Assert.Single(concurrent, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(concurrent, r => r.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in concurrent) response.Dispose();
        await using var db = h.App.OwnerContext();
        var parents = await db.Set<Category>().ToDictionaryAsync(z => z.Id, z => z.ParentId);
        Assert.True(TeaPremium.Domain.Catalog.TaxonomyRules.ValidTree(parents));
    }

    [Theory]
    [InlineData("categories", "/danh-muc/", "visible", "hidden")]
    [InlineData("collections", "/bo-suu-tap/", "visible", "hidden")]
    [InlineData("materials", "/chat-lieu/", "published", "archived")]
    [InlineData("artisans", "/nghe-nhan/", "published", "archived")]
    public async Task FirstPublicationHiddenArchiveRenameFlattenReuseAndRepublishAreAtomicAndPermanent(string resource, string prefix, string publicStatus, string hiddenStatus)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var entry = await h.CreateAsync(resource, new { name = "A", slug = "draft-a", kind = "clay" });
        entry = await h.PatchAsync(resource, entry, new { slug = "a" });
        await using (var db = h.App.OwnerContext()) Assert.Empty(await db.Set<Redirect>().ToArrayAsync());
        entry = resource is "materials" or "artisans" ? await h.PublishViewAsync(resource, entry) : await h.PatchAsync(resource, entry, new { status = publicStatus });
        Assert.Equal(h.Clock.UtcNow.UtcDateTime, entry.FirstPublishedAt);
        var timestamp = entry.FirstPublishedAt; h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(10);
        entry = await h.PatchAsync(resource, entry, new { status = hiddenStatus });
        entry = await h.PatchAsync(resource, entry, new { slug = "b" });
        entry = await h.PatchAsync(resource, entry, new { slug = "c" });
        await using (var db = h.App.OwnerContext())
        {
            var redirects = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
            Assert.Equal(prefix + "c", redirects[prefix + "a"]); Assert.Equal(prefix + "c", redirects[prefix + "b"]);
        }
        entry = await h.PatchAsync(resource, entry, new { slug = "a" });
        entry = resource is "materials" or "artisans" ? await h.PublishViewAsync(resource, entry) : await h.PatchAsync(resource, entry, new { status = publicStatus });
        Assert.Equal(timestamp, entry.FirstPublishedAt);
        await using (var db = h.App.OwnerContext())
        {
            var redirects = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
            Assert.False(redirects.ContainsKey(prefix + "a")); Assert.Equal(prefix + "a", redirects[prefix + "c"]);
            Assert.All(redirects, r => Assert.False(redirects.ContainsKey(r.Value)));
            var audits = await db.AuditLogs.Where(x => x.EntityId == entry.Id).ToArrayAsync(); Assert.NotEmpty(audits);
            Assert.All(audits, log => Assert.DoesNotContain("biography@example.test", log.Changes, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("materials", 0)] [InlineData("materials", 399)] [InlineData("materials", 400)] [InlineData("materials", 401)]
    [InlineData("artisans", 0)] [InlineData("artisans", 399)] [InlineData("artisans", 400)] [InlineData("artisans", 401)]
    public async Task PublishReturnsApprovedNonBlockingWarningAndNeverChangesNoindex(string resource, int words)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var fields = new Dictionary<string, object?> { ["name"] = resource == "materials" ? "Tử nê" : "Chu Bân", ["kind"] = "clay", ["robotsNoindex"] = true,
            [resource == "materials" ? "descriptionHtml" : "bioHtml"] = words == 0 ? null : "<p>" + string.Join("&nbsp;", Enumerable.Repeat("từ", words)) + "</p><script>not counted words</script>",
            ["summary"] = string.Join(' ', Enumerable.Repeat("not counted", 500)) };
        var entry = await h.CreateAsync(resource, fields);
        using var response = await h.PublishAsync(resource, entry); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = (await response.Content.ReadFromJsonAsync<CatalogPublication>())!; Assert.Equal("published", published.Status); Assert.NotEqual(entry.RowVersion, published.RowVersion);
        Assert.Equal(words < 400 ? 1 : 0, published.Warnings.Count);
        if (words < 400)
        {
            var warning = Assert.Single(published.Warnings); Assert.Equal("content_below_recommended_words", warning.Code);
            Assert.Equal(resource == "materials" ? "descriptionHtml" : "bioHtml", warning.Field); Assert.Equal(words, warning.ActualWords); Assert.Equal(400, warning.RecommendedWords);
        }
        var current = await h.GetAsync(resource, entry.Id); Assert.True(current.RobotsNoindex);
        Assert.DoesNotContain("script", current.DescriptionHtml ?? current.BioHtml ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("materials", "Tử nê", "Tử nê: đặc điểm, cách chọn và tác phẩm", "Thông tin về chất liệu Tử nê.")]
    [InlineData("artisans", "Chu Bân", "Nghệ nhân Chu Bân", "Thông tin về nghệ nhân Chu Bân.")]
    public async Task SeoFallbackMissingSourcesExplicitValuesAndBrandDoNotMutateStoredSeo(string resource, string name, string title, string description)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var entry = await h.CreateAsync(resource, new { name, kind = "clay" });
        Assert.Equal(title, entry.ResolvedSeoTitle); Assert.Equal(description, entry.ResolvedSeoDescription);
        Assert.Null(entry.SeoTitle); Assert.Null(entry.SeoDescription);
        entry = await h.PatchAsync(resource, entry, new { seoTitle = "Explicit", seoDescription = "Explicit description" });
        Assert.Equal("Explicit", entry.ResolvedSeoTitle); Assert.Equal("Explicit description", entry.ResolvedSeoDescription);
        await h.App.ExecuteAsync("INSERT INTO platform.settings(key,value) VALUES('seo.brand_name','\"Fixture brand\"')");
        entry = await h.PatchAsync(resource, entry, new { seoTitle = (string?)null, seoDescription = (string?)null });
        if (resource == "artisans") Assert.Equal("Nghệ nhân Chu Bân | Fixture brand", entry.ResolvedSeoTitle);
        Assert.Null(entry.SeoTitle); Assert.Null(entry.SeoDescription);
    }

    [Theory]
    [InlineData("clay")] [InlineData("metal")] [InlineData("glass")] [InlineData("ceramic")]
    [InlineData("porcelain")] [InlineData("wood")] [InlineData("stone")] [InlineData("other")]
    public async Task MaterialKindsNullableAndReservedSkuCodes(string kind)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var entry = await h.CreateAsync("materials", new { name = "Reference material", kind }); Assert.Null(entry.SkuCode);
        entry = await h.PatchAsync("materials", entry, new { skuCode = "TN" });
        using (var duplicate = await h.CreateResponseAsync("materials", new { name = "Duplicate code", kind, skuCode = "TN" })) Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        foreach (var invalid in new[] { "XX", "x", "abcd", "T1" })
        {
            using var response = await h.PatchResponseAsync("materials", entry, new { skuCode = invalid }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        entry = await h.PatchAsync("materials", entry, new { skuCode = (string?)null }); Assert.Null(entry.SkuCode);
        using var wrongKind = await h.PatchResponseAsync("materials", entry, new { kind = "invalid" }); Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
    }

    [Fact]
    public async Task MediaReferencesAndCrossTypeFieldsAreValidatedWithoutPrivateDisclosure()
    {
        await using var h = await Harness.CreateAsync(fixture); await h.App.ExecuteAsync(CatalogInventoryFixture.Seed);
        var material = await h.CreateAsync("materials", new { name = "M", kind = "clay", coverMediaId = "00000000-0000-0000-0000-000000000005" });
        using (var missing = await h.PatchResponseAsync("materials", material, new { coverMediaId = Guid.NewGuid() })) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await h.App.ExecuteAsync("UPDATE media.assets SET purpose='evidence' WHERE id='00000000-0000-0000-0000-000000000006'");
        var artisan = await h.CreateAsync("artisans", new { name = "A", portraitMediaId = "00000000-0000-0000-0000-000000000005" });
        using (var evidence = await h.PatchResponseAsync("artisans", artisan, new { portraitMediaId = "00000000-0000-0000-0000-000000000006" })) Assert.Equal(HttpStatusCode.BadRequest, evidence.StatusCode);
        using var crossType = await h.App.MutateAsync(HttpMethod.Patch, Root + "materials/" + material.Id, new { rowVersion = material.RowVersion, bioHtml = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, crossType.StatusCode);
        var json = JsonSerializer.Serialize(material); Assert.DoesNotContain("StorageKey", json, StringComparison.Ordinal); Assert.DoesNotContain("avgCost", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectionProductReplacementHasOrderingNoDuplicatesNoForeignIdsAndRollback()
    {
        await using var h = await Harness.CreateAsync(fixture); await h.SeedTwoProductsAsync();
        var collection = await h.CreateAsync("collections", new { name = "Collection" });
        var items = new[] { new CollectionProductView(Guid.Parse(CatalogInventoryFixture.ProductId), 9), new CollectionProductView(Harness.SecondProduct, 2) };
        using var put = await h.App.MutateAsync(HttpMethod.Put, Root + "collections/" + collection.Id + "/products", new { rowVersion = collection.RowVersion, items });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode); var list = (await put.Content.ReadFromJsonAsync<CollectionProductList>())!;
        Assert.Equal(Harness.SecondProduct, list.Items[0].ProductId); Assert.NotEqual(collection.RowVersion, list.RowVersion);
        foreach (var invalid in new[] { new[] { items[0], items[0] }, new[] { new CollectionProductView(Guid.NewGuid(), 0) } })
        {
            using var response = await h.App.MutateAsync(HttpMethod.Put, Root + "collections/" + collection.Id + "/products", new { list.RowVersion, items = invalid }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await h.App.ExecuteAsync("""
CREATE FUNCTION catalog.reject_collection_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.sort_order=99 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$;
CREATE TRIGGER reject_collection_fixture BEFORE INSERT OR UPDATE ON catalog.collection_products FOR EACH ROW EXECUTE FUNCTION catalog.reject_collection_fixture();
""");
        using var failed = await h.App.MutateAsync(HttpMethod.Put, Root + "collections/" + collection.Id + "/products", new { list.RowVersion, items = new[] { new CollectionProductView(items[0].ProductId, 99) } });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var get = await h.App.Client.GetAsync(Root + "collections/" + collection.Id + "/products");
        var after = (await get.Content.ReadFromJsonAsync<CollectionProductList>())!; Assert.Equal(list.RowVersion, after.RowVersion); Assert.Equal(list.Items.ToArray(), after.Items.ToArray());
    }

    [Fact]
    public async Task FailedRedirectWriteRollsBackSlugStateAuditAndFirstPublication()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var category = await h.CreateAsync("categories", new { name = "A", status = "visible" });
        await h.App.ExecuteAsync("""
CREATE FUNCTION content.reject_redirect_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture secret@example.test'; END $$;
CREATE TRIGGER reject_redirect_fixture BEFORE INSERT OR UPDATE ON content.redirects FOR EACH ROW EXECUTE FUNCTION content.reject_redirect_fixture();
""");
        await using var beforeDb = h.App.OwnerContext(); var audits = await beforeDb.AuditLogs.CountAsync(x => x.EntityId == category.Id);
        using var failed = await h.PatchResponseAsync("categories", category, new { slug = "b", status = "hidden" }); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var after = await h.GetAsync("categories", category.Id); Assert.Equal(category.RowVersion, after.RowVersion); Assert.Equal("a", after.Slug); Assert.Equal("visible", after.Status);
        Assert.DoesNotContain("secret@example.test", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = h.App.OwnerContext(); Assert.Empty(await db.Set<Redirect>().ToArrayAsync()); Assert.Equal(audits, await db.AuditLogs.CountAsync(x => x.EntityId == category.Id));
        Assert.Equal(category.FirstPublishedAt, after.FirstPublishedAt);
    }

    [Fact]
    public async Task ConcurrentSlugAndStatusMutationsHaveOneWinnerAndNoLostHistory()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var category = await h.CreateAsync("categories", new { name = "Concurrent", status = "visible" });
        var responses = await Task.WhenAll(h.PatchResponseAsync("categories", category, new { slug = "renamed" }), h.PatchResponseAsync("categories", category, new { status = "hidden" }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var current = await h.GetAsync("categories", category.Id); Assert.Equal(category.FirstPublishedAt, current.FirstPublishedAt);
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task DraftReusingHistoricalSlugAcquiresNoHistoryAndPublicationReclaimsItsRoute()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var original = await h.CreateAsync("materials", new { name = "Original", slug = "old", kind = "clay" });
        original = await h.PublishViewAsync("materials", original);
        await h.PatchAsync("materials", original, new { slug = "current" });
        var draft = await h.CreateAsync("materials", new { name = "New draft", slug = "old", kind = "wood" });
        Assert.Null(draft.FirstPublishedAt);
        await using (var db = h.App.OwnerContext()) Assert.Equal("/chat-lieu/current", (await db.Set<Redirect>().SingleAsync()).TargetPath);
        draft = await h.PublishViewAsync("materials", draft);
        Assert.NotNull(draft.FirstPublishedAt);
        await using (var db = h.App.OwnerContext()) Assert.Empty(await db.Set<Redirect>().ToArrayAsync());
    }

    [Fact]
    public async Task FailedPublicationAndFailedOutboxInsertionRollBackAllBusinessState()
    {
        await using var h = await Harness.CreateAsync(fixture); await h.SeedTwoProductsAsync();
        var material = await h.CreateAsync("materials", new { name = "Before", kind = "clay", skuCode = "TN" });
        await h.App.ExecuteAsync($"UPDATE catalog.products SET material_id='{material.Id}'");
        await h.App.ExecuteAsync("""
CREATE FUNCTION catalog.reject_publish_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status='published' THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$;
CREATE TRIGGER reject_publish_fixture BEFORE UPDATE ON catalog.materials FOR EACH ROW EXECUTE FUNCTION catalog.reject_publish_fixture();
CREATE FUNCTION platform.reject_rebuild_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.type='RebuildProductSearchText' THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$;
CREATE TRIGGER reject_rebuild_fixture BEFORE INSERT ON platform.outbox_messages FOR EACH ROW EXECUTE FUNCTION platform.reject_rebuild_fixture();
""");
        using (var failedPublish = await h.PublishAsync("materials", material)) Assert.Equal(HttpStatusCode.InternalServerError, failedPublish.StatusCode);
        var after = await h.GetAsync("materials", material.Id); Assert.Equal("draft", after.Status); Assert.Null(after.FirstPublishedAt); Assert.Equal(material.RowVersion, after.RowVersion);
        using (var failedRename = await h.PatchResponseAsync("materials", material, new { name = "After", skuCode = "MO" })) Assert.Equal(HttpStatusCode.InternalServerError, failedRename.StatusCode);
        after = await h.GetAsync("materials", material.Id); Assert.Equal("Before", after.Name); Assert.Equal("TN", after.SkuCode); Assert.Equal(material.RowVersion, after.RowVersion);
        await using var db = h.App.OwnerContext(); Assert.Empty(await db.OutboxMessages.ToArrayAsync()); Assert.Single(await db.AuditLogs.Where(x => x.EntityId == material.Id).ToArrayAsync());
    }

    [Fact]
    public async Task UnknownLegacyPublicTimestampIsNeverGuessedAndNoindexReasonIsRetainedProtected()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var material = await h.CreateAsync("materials", new { name = "Legacy shape", kind = "clay" });
        // Synthetic unknown-history row, never evidence of a deployable production dataset.
        await h.App.ExecuteAsync($"UPDATE catalog.materials SET status='published' WHERE id='{material.Id}'");
        material = await h.GetAsync("materials", material.Id);
        using (var patch = await h.PatchResponseAsync("materials", material, new { name = "Do not guess" })) Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        using (var publish = await h.PublishAsync("materials", material)) Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Null((await h.GetAsync("materials", material.Id)).FirstPublishedAt);
        var category = await h.CreateAsync("categories", new { name = "Noindex" });
        using (var noReason = await h.PatchResponseAsync("categories", category, new { robotsNoindex = true })) Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        const string reason = "Review private-note@example.test before indexing";
        category = await h.PatchAsync("categories", category, new { robotsNoindex = true, noindexReason = reason }); Assert.True(category.RobotsNoindex);
        await using var db = h.App.OwnerContext(); var logs = await db.AuditLogs.Where(x => x.EntityId == category.Id).ToArrayAsync();
        Assert.All(logs, log => Assert.DoesNotContain("private-note@example.test", log.Changes, StringComparison.Ordinal));
        using var changes = JsonDocument.Parse(Assert.Single(logs, x => x.Action == "state_change").Changes);
        var protectedReason = changes.RootElement.GetProperty("reason").GetString()!;
        Assert.Equal(reason, h.App.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("teapremium.catalog.noindex-reason.v1").Unprotect(protectedReason));
    }

    [Fact]
    public async Task ConcurrentAutomaticSlugsAndMalformedJsonAreHandledDeterministically()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var created = await Task.WhenAll(h.CreateResponseAsync("materials", new { name = "Same", kind = "clay" }), h.CreateResponseAsync("materials", new { name = "Same", kind = "clay" }));
        Assert.All(created, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var views = await Task.WhenAll(created.Select(x => x.Content.ReadFromJsonAsync<CatalogView>()));
        Assert.Equal(ConcurrentSlugs, views.Select(x => x!.Slug).Order(StringComparer.Ordinal));
        foreach (var response in created) response.Dispose();
        using var csrf = await h.App.Client.GetAsync(Root + "auth/csrf");
        var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "materials") { Content = new StringContent("{bad json", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF-TOKEN", token); using var malformed = await h.App.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode); Assert.Equal("application/problem+json", malformed.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task RenamingReferencesCommitsSearchOutboxAndRebuildIsCurrentStateIdempotentWithoutChangingSkus()
    {
        await using var h = await Harness.CreateAsync(fixture); await h.SeedTwoProductsAsync();
        // A synthetic reference row has the same schema shape as the future P1-03 seed, no seed CLI duplicated.
        var material = await h.CreateAsync("materials", new { name = "Tử nê", slug = "tu-ne", kind = "clay", skuCode = "TN", summary = "Seed metadata" });
        var artisan = await h.CreateAsync("artisans", new { name = "Chu Bân" });
        await h.App.ExecuteAsync($"UPDATE catalog.products SET material_id='{material.Id}', artisan_id='{artisan.Id}'");
        material = await h.PatchAsync("materials", material, new { name = "Chất đất mới", skuCode = "MO" });
        artisan = await h.PatchAsync("artisans", artisan, new { name = "Nghệ nhân mới" });
        Assert.Equal("Seed metadata", material.Summary);
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>(); Assert.Equal(2, await processor.ProcessAsync(10, default));
        }
        await using var db = h.App.OwnerContext();
        var products = await db.Set<Product>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.All(products, product => { Assert.Contains("chat dat moi", product.SearchText, StringComparison.Ordinal); Assert.Contains("nghe nhan moi", product.SearchText, StringComparison.Ordinal); });
        Assert.Equal("AM-XX-0001", products[0].SkuBase); Assert.Equal("AM-XX-0001", (await db.Set<Variant>().SingleAsync()).Sku);
        var message = await db.OutboxMessages.FirstAsync(); Assert.Equal("processed", message.Status);
        await h.App.ExecuteAsync($"UPDATE platform.outbox_messages SET status='pending' WHERE id='{message.Id}'");
        await using (var scope = h.App.Factory.Services.CreateAsyncScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
    }

    [Fact]
    public async Task AnonymousMissingPermissionsCsrfAndPatchPublishBypassAreDenied()
    {
        await using var h = await Harness.CreateAsync(fixture, false);
        using (var anonymous = await h.App.Client.GetAsync(Root + "materials")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await h.App.LoginAsync(); var material = await h.CreateAsync("materials", new { name = "M", kind = "clay" });
        using (var missing = await h.App.Client.PostAsJsonAsync(Root + "materials", new { name = "No CSRF", kind = "clay" })) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Post, Root + "materials") { Content = JsonContent.Create(new { name = "Invalid CSRF", kind = "clay" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", "invalid"); using var invalid = await h.App.Client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var bypass = await h.PatchResponseAsync("materials", material, new { status = "published" })) Assert.Equal(HttpStatusCode.Forbidden, bypass.StatusCode);
        using (var createPublished = await h.CreateResponseAsync("materials", new { name = "Bypass", kind = "clay", status = "published" })) Assert.Equal(HttpStatusCode.Forbidden, createPublished.StatusCode);
        var contentEmail = await h.CreateStaffAsync("content");
        await h.App.ExecuteAsync("DELETE FROM iam.role_claims WHERE claim_value='catalog.publish' AND role_id IN (SELECT id FROM iam.roles WHERE name='content')");
        await h.App.LoginAsync(contentEmail, P009Harness.Password + "-Changed");
        using (var denied = await h.PublishAsync("materials", material)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await h.PatchResponseAsync("materials", material, new { status = "published" })) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await h.App.ExecuteAsync("DELETE FROM iam.role_claims WHERE claim_value='catalog.read' AND role_id IN (SELECT id FROM iam.roles WHERE name='content')");
        using (var read = await h.App.Client.GetAsync(Root + "materials")) Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        var salesEmail = await h.CreateStaffAsync("sales"); await h.App.LoginAsync(salesEmail, P009Harness.Password + "-Changed");
        using (var write = await h.PatchResponseAsync("materials", material, new { name = "Denied" })) Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await using var db = h.App.OwnerContext(); Assert.Equal("draft", (await db.Set<Material>().SingleAsync()).Status);
    }

    [Fact]
    public async Task OpenApiHasTwentyTaxonomyOperationsAndTypedWriteAndWarningContracts()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient(); using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        var count = paths.EnumerateObject().Where(p => Resources.Any(r => p.Name.StartsWith(Root + r, StringComparison.Ordinal)))
            .Sum(p => p.Value.EnumerateObject().Count(x => x.Name is "get" or "post" or "patch" or "put"));
        Assert.Equal(20, count);
        Assert.Contains("MaterialWriteRequest", paths.GetProperty(Root + "materials").GetProperty("post").GetProperty("requestBody").GetRawText(), StringComparison.Ordinal);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas"); Assert.True(schemas.TryGetProperty("ContentWarning", out _)); Assert.True(schemas.TryGetProperty("CatalogPublication", out _));
    }

    private sealed class MutableClock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
    private sealed class Harness(P009Harness app, MutableClock clock) : IAsyncDisposable
    {
        public static readonly Guid SecondProduct = Guid.Parse("00000000-0000-0000-0000-000000000022");
        public P009Harness App => app;
        public MutableClock Clock => clock;
        public static async Task<Harness> CreateAsync(PlatformDatabaseFixture fixture, bool login = true)
        {
            var clock = new MutableClock(); var app = await P009Harness.CreateAsync(fixture, services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock); });
            if (login) await app.LoginAsync(); return new(app, clock);
        }
        private static Dictionary<string, object?> Fields(string resource, object input)
        {
            var fields = JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone(), StringComparer.Ordinal);
            // Shared fixture convenience only; actual API explicitly rejects cross-kind fields.
            if (resource != "materials") { fields.Remove("kind"); fields.Remove("summary"); }
            return fields;
        }
        public Task<HttpResponseMessage> CreateResponseAsync(string resource, object input) => app.MutateAsync(HttpMethod.Post, Root + resource, Fields(resource, input));
        public async Task<CatalogView> CreateAsync(string resource, object input)
        {
            using var response = await CreateResponseAsync(resource, input); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CatalogView>())!;
        }
        public Task<HttpResponseMessage> PatchResponseAsync(string resource, CatalogView view, object input)
        {
            var fields = Fields(resource, input); fields["rowVersion"] = view.RowVersion;
            return app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + view.Id, fields);
        }
        public async Task<CatalogView> PatchAsync(string resource, CatalogView view, object input)
        {
            using var response = await PatchResponseAsync(resource, view, input); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CatalogView>())!;
        }
        public Task<HttpResponseMessage> PublishAsync(string resource, CatalogView view) => app.MutateAsync(HttpMethod.Post, Root + resource + "/" + view.Id + "/publish", new { view.RowVersion });
        public async Task<CatalogView> PublishViewAsync(string resource, CatalogView view)
        {
            using var response = await PublishAsync(resource, view); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await GetAsync(resource, view.Id);
        }
        public async Task<CatalogView> GetAsync(string resource, Guid id) => (await app.Client.GetFromJsonAsync<CatalogView>(Root + resource + "/" + id))!;
        public Task SeedTwoProductsAsync() => app.ExecuteAsync(CatalogInventoryFixture.Seed + """
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES ('00000000-0000-0000-0000-000000000022','00000000-0000-0000-0000-000000000001','AM-XX-0002','second-product','Second product');
""");
        public async Task<string> CreateStaffAsync(string role)
        {
            var email = role + Guid.NewGuid().ToString("N") + "@example.test";
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>().Handle(new(email, "Fixture", P009Harness.Password, [role], app.OwnerId, app.OwnerStamp), default);
            Assert.True(created.IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>().Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
            return email;
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
