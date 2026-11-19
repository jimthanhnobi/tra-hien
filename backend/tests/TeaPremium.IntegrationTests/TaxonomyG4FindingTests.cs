using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-04 focused G4 P1/P2 / BR-CAT-08, BR-SEO-31/32/35, NFR-07/09.
public sealed class TaxonomyG4FindingTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/";
    [Fact]
    public async Task RedirectLockIsReleasedAfterRollbackAndRetryPreservesTheOtherPrefixCommit()
    {
        var gate = new RedirectSnapshotGate();
        await using var app = await P009Harness.CreateAsync(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        await app.LoginAsync();
        var material = await PublishAsync(app, "materials", await CreateAsync(app, "materials", new { name = "Material", slug = "a", kind = "clay" }));
        var artisan = await PublishAsync(app, "artisans", await CreateAsync(app, "artisans", new { name = "Artisan", slug = "x" }));
        await app.ExecuteAsync("""
INSERT INTO content.redirects(source_path,target_path) VALUES('/nghe-nhan/y','/chat-lieu/a');
CREATE FUNCTION catalog.fail_graph_rename() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.slug='b' THEN RAISE EXCEPTION 'fixture failure'; END IF; RETURN NEW; END $$;
CREATE TRIGGER fail_graph_rename BEFORE UPDATE ON catalog.materials FOR EACH ROW EXECUTE FUNCTION catalog.fail_graph_rename();
""");
        gate.Enable();
        var first = PatchAsync(app, "materials", material, new { slug = "b" });
        Task<HttpResponseMessage>? second = null;
        var blocked = false;
        try
        {
            await gate.SnapshotPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = PatchAsync(app, "artisans", artisan, new { slug = "y" });
            for (var i = 0; i < 500 && !blocked; i++)
            {
                await using var db = app.OwnerContext();
                blocked = await db.Database.SqlQueryRaw<bool>("""
SELECT EXISTS(SELECT FROM pg_locks WHERE NOT granted AND locktype='advisory' AND classid=8104 AND objid=100
 AND objsubid=2 AND database=(SELECT oid FROM pg_database WHERE datname=current_database())) AS "Value"
""").SingleAsync();
                if (!blocked) await Task.Delay(10);
            }
        }
        finally { gate.ReleaseSnapshot.TrySetResult(); }
        using var failed = await first.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var reclaimed = await (second ?? throw new InvalidOperationException("Second transaction did not start.")).WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(HttpStatusCode.OK, reclaimed.StatusCode);
        Assert.True(blocked);
        var afterRollback = await GetAsync(app, "materials", material.Id);
        Assert.Equal(material.RowVersion, afterRollback.RowVersion); Assert.Equal("a", afterRollback.Slug); Assert.Equal(material.FirstPublishedAt, afterRollback.FirstPublishedAt);
        await using (var db = app.OwnerContext())
        {
            var redirects = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
            Assert.Single(redirects); Assert.Equal("/nghe-nhan/y", redirects["/nghe-nhan/x"]);
        }
        await app.ExecuteAsync("DROP TRIGGER fail_graph_rename ON catalog.materials; DROP FUNCTION catalog.fail_graph_rename()");
        using var retry = await PatchAsync(app, "materials", material, new { slug = "b" }); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var check = app.OwnerContext(); var final = await check.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
        Assert.Equal("/chat-lieu/b", final["/chat-lieu/a"]); Assert.Equal("/nghe-nhan/y", final["/nghe-nhan/x"]);
        Assert.False(final.ContainsKey("/nghe-nhan/y")); Assert.All(final, x => Assert.False(final.ContainsKey(x.Value)));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task RedirectGraphSnapshotSerializesCrossPrefixReclamationInEitherOrderAndSamePrefix(bool artisanFirst, bool samePrefix)
    {
        var gate = new RedirectSnapshotGate();
        await using var app = await P009Harness.CreateAsync(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        await app.LoginAsync();
        var material = await CreateAsync(app, "materials", new { name = "Material", slug = "a", kind = "clay" });
        material = await PublishAsync(app, "materials", material);
        var otherResource = samePrefix ? "materials" : "artisans";
        var otherPrefix = samePrefix ? "/chat-lieu/" : "/nghe-nhan/";
        var other = await CreateAsync(app, otherResource, samePrefix ? (object)new { name = "Other", slug = "x", kind = "wood" } : new { name = "Artisan", slug = "x" });
        other = await PublishAsync(app, otherResource, other);
        await app.ExecuteAsync($"INSERT INTO content.redirects(source_path,target_path) VALUES ('{otherPrefix}y','/chat-lieu/a'),('/unrelated','/stable')");
        gate.Enable();
        var first = PatchAsync(app, artisanFirst ? otherResource : "materials", artisanFirst ? other : material, new { slug = artisanFirst ? "y" : "b" });
        Task<HttpResponseMessage>? second = null;
        var blocked = false;
        try
        {
            await gate.SnapshotPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = PatchAsync(app, artisanFirst ? "materials" : otherResource, artisanFirst ? material : other, new { slug = artisanFirst ? "b" : "y" });
            for (var i = 0; i < 500; i++)
            {
                await using var db = app.OwnerContext();
                // pg_locks exposes lock identity without grants to read other users' SQL/PII.
                blocked = await db.Database.SqlQueryRaw<bool>("""
SELECT EXISTS(SELECT FROM pg_locks WHERE NOT granted AND locktype='advisory'
 AND classid=8104 AND objsubid=2 AND database=(SELECT oid FROM pg_database WHERE datname=current_database())) AS "Value"
""").SingleAsync();
                if (blocked || gate.SecondSnapshot.Task.IsCompleted) break;
                await Task.Delay(10);
            }
            // In the original bug T2 reads/commits a stale graph while T1 is paused after its SELECT.
            if (gate.SecondSnapshot.Task.IsCompleted) using (await second.WaitAsync(TimeSpan.FromSeconds(10))) { }
        }
        finally { gate.ReleaseSnapshot.TrySetResult(); }
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(10));
        using var secondResponse = await (second ?? throw new InvalidOperationException("Second transaction did not start.")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode); Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.True(blocked, "Competing transaction must wait before reading the shared redirect snapshot.");
        await using var check = app.OwnerContext();
        var redirects = await check.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
        Assert.False(redirects.ContainsKey(otherPrefix + "y"));
        Assert.Equal(otherPrefix + "y", redirects[otherPrefix + "x"]);
        Assert.Equal("/chat-lieu/b", redirects["/chat-lieu/a"]); Assert.Equal("/stable", redirects["/unrelated"]);
        Assert.All(redirects, pair => Assert.False(redirects.ContainsKey(pair.Value)));
        Assert.Equal(material.FirstPublishedAt, (await GetAsync(app, "materials", material.Id)).FirstPublishedAt);
        Assert.Equal(other.FirstPublishedAt, (await GetAsync(app, otherResource, other.Id)).FirstPublishedAt);
        using var stale = await PatchAsync(app, "materials", material, new { slug = "stale" }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Theory]
    [InlineData("materials", "descriptionHtml", 399)] [InlineData("materials", "descriptionHtml", 400)] [InlineData("materials", "descriptionHtml", 401)]
    [InlineData("artisans", "bioHtml", 399)] [InlineData("artisans", "bioHtml", 400)] [InlineData("artisans", "bioHtml", 401)]
    public async Task PersistedSanitizedAdjacentBlocksUseTheSameWordBoundariesAsSeoAndPublish(string resource, string field, int words)
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        var html = string.Concat(Enumerable.Range(0, words).Select(i => i % 2 == 0 ? "<div>từ</div>" : "<section><div>từ</div></section>"))
            + "<script>not counted</script><style>not counted</style>";
        var fields = new Dictionary<string, object?> { ["name"] = "Tiếng Việt", [field] = html };
        if (resource == "materials") fields["kind"] = "clay";
        var entry = await CreateAsync(app, resource, fields);
        var source = resource == "materials" ? entry.DescriptionHtml : entry.BioHtml;
        Assert.NotNull(source); Assert.DoesNotContain("<script", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<section", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("từtừ", source, StringComparison.Ordinal);
        Assert.StartsWith("từ từ ", entry.ResolvedSeoDescription, StringComparison.Ordinal);
        using var response = await app.MutateAsync(HttpMethod.Post, Root + resource + "/" + entry.Id + "/publish", new { entry.RowVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = (await response.Content.ReadFromJsonAsync<CatalogPublication>())!;
        if (words < 400) { var warning = Assert.Single(published.Warnings); Assert.Equal(words, warning.ActualWords); Assert.Equal(field, warning.Field); }
        else Assert.Empty(published.Warnings);
        var persisted = await GetAsync(app, resource, entry.Id);
        Assert.Equal(source, resource == "materials" ? persisted.DescriptionHtml : persisted.BioHtml);
        Assert.False(persisted.RobotsNoindex);
    }

    [Fact]
    public async Task NullCollectionItemsReturn400AndPreserveRelationshipsVersionAuditAndOutbox()
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync(); await app.ExecuteAsync(CatalogInventoryFixture.Seed);
        var entry = await CreateAsync(app, "collections", new { name = "Collection" });
        var valid = new CollectionProductView(Guid.Parse(CatalogInventoryFixture.ProductId), 7);
        var path = Root + "collections/" + entry.Id + "/products";
        using var initial = await app.MutateAsync(HttpMethod.Put, path, new { entry.RowVersion, items = new[] { valid } });
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode); var list = (await initial.Content.ReadFromJsonAsync<CollectionProductList>())!;
        var before = await FingerprintAsync(app);
        foreach (var invalid in new object?[][] { [null], [valid, null], [valid, valid], [new CollectionProductView(Guid.NewGuid(), 1)] })
        {
            using var response = await app.MutateAsync(HttpMethod.Put, path, new { list.RowVersion, items = invalid });
            await AssertValidationAsync(response);
            Assert.Equal(before, await FingerprintAsync(app));
            var after = (await app.Client.GetFromJsonAsync<CollectionProductList>(path))!;
            Assert.Equal(list.RowVersion, after.RowVersion); Assert.Equal(list.Items.ToArray(), after.Items.ToArray());
        }
        using var empty = await app.MutateAsync(HttpMethod.Put, path, new { list.RowVersion, items = Array.Empty<CollectionProductView>() });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode); Assert.Empty((await empty.Content.ReadFromJsonAsync<CollectionProductList>())!.Items);
    }

    [Theory]
    [InlineData("categories")] [InlineData("collections")] [InlineData("materials")] [InlineData("artisans")]
    public async Task NulRejectedInEveryWritableTextFieldAndNestedJsonAndQueryBeforeDatabaseChanges(string resource)
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        var initial = new Dictionary<string, object?> { ["name"] = "Tiếng Việt hợp lệ" }; if (resource == "materials") initial["kind"] = "clay";
        var entry = await CreateAsync(app, resource, initial);
        string[] extra = resource switch
        {
            "materials" => ["kind", "skuCode", "summary", "descriptionHtml"], "artisans" => ["title", "bioHtml"],
            _ => ["descriptionHtml", "bodyHtml"]
        };
        var before = await FingerprintAsync(app);
        foreach (var field in extra.Concat(["name", "slug", "status", "focusKeyword", "seoTitle", "seoDescription", "noindexReason", "rowVersion"]))
        {
            var create = new Dictionary<string, object?>(initial) { [field] = "a\0b" };
            using (var response = await app.MutateAsync(HttpMethod.Post, Root + resource, create)) await AssertValidationAsync(response);
            var patch = new Dictionary<string, object?> { ["rowVersion"] = entry.RowVersion, [field] = "a\0b" };
            using (var response = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + entry.Id, patch)) await AssertValidationAsync(response);
            Assert.Equal(before, await FingerprintAsync(app));
        }
        var jsonField = resource switch { "materials" => "descriptionJson", "artisans" => "bioJson", _ => "bodyJson" };
        foreach (var json in new object[]
        {
            new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = "a\0b" } } } } },
            new Dictionary<string, object?> { ["type"] = "doc", ["attrs"] = new Dictionary<string, object?> { ["a\0b"] = "value" } }
        })
        {
            var create = new Dictionary<string, object?>(initial) { [jsonField] = json };
            using (var response = await app.MutateAsync(HttpMethod.Post, Root + resource, create)) await AssertValidationAsync(response);
            using (var response = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + entry.Id, new Dictionary<string, object?> { ["rowVersion"] = entry.RowVersion, [jsonField] = json })) await AssertValidationAsync(response);
        }
        using (var query = await app.Client.GetAsync(Root + resource + "?q=a%00b")) await AssertValidationAsync(query);
        using (var query = await app.Client.GetAsync(Root + resource + "?status=a%00b")) await AssertValidationAsync(query);
        Assert.Equal(before, await FingerprintAsync(app));
        Assert.Equal(entry.RowVersion, (await GetAsync(app, resource, entry.Id)).RowVersion);
        var contentField = resource == "artisans" ? "bioHtml" : "descriptionHtml";
        using var good = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + entry.Id, new Dictionary<string, object?>
        {
            ["rowVersion"] = entry.RowVersion, [contentField] = "<p>Tử nê\n\tChu Bân&nbsp;đẹp</p>", ["seoDescription"] = null
        });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using var goodQuery = await app.Client.GetAsync(Root + resource + "?q=" + Uri.EscapeDataString("Tiếng Việt\n\t")); Assert.Equal(HttpStatusCode.OK, goodQuery.StatusCode);
    }

    private static async Task<string> FingerprintAsync(P009Harness app)
    {
        await using var db = app.OwnerContext();
        return await db.Database.SqlQueryRaw<string>("""
SELECT md5(concat(
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.categories t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.collections t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.materials t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.artisans t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY collection_id,product_id) FROM catalog.collection_products t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.audit_logs t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.outbox_messages t))) AS "Value"
""").SingleAsync();
    }
    private static async Task AssertValidationAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
    }
    private static async Task<CatalogView> CreateAsync(P009Harness app, string resource, object fields)
    {
        using var response = await app.MutateAsync(HttpMethod.Post, Root + resource, fields); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CatalogView>())!;
    }
    private static Task<CatalogView> GetAsync(P009Harness app, string resource, Guid id) => app.Client.GetFromJsonAsync<CatalogView>(Root + resource + "/" + id)!;
    private static Task<HttpResponseMessage> PatchAsync(P009Harness app, string resource, CatalogView view, object fields)
    {
        var values = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        values["rowVersion"] = view.RowVersion; return app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + view.Id, values);
    }
    private static async Task<CatalogView> PublishAsync(P009Harness app, string resource, CatalogView view)
    {
        using var response = await app.MutateAsync(HttpMethod.Post, Root + resource + "/" + view.Id + "/publish", new { view.RowVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await GetAsync(app, resource, view.Id);
    }
    private sealed class RedirectSnapshotGate : DbCommandInterceptor
    {
        private int enabled;
        private int snapshots;
        public TaskCompletionSource SnapshotPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enable() => Volatile.Write(ref enabled, 1);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref enabled) == 1 && command.CommandText.Contains("FROM content.redirects", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref snapshots) == 1)
                { SnapshotPaused.TrySetResult(); await ReleaseSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
                else SecondSnapshot.TrySetResult();
            }
            return result;
        }
    }
}
