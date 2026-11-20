using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-04 integration with merged P1-03: BR-CAT-10/14, FR-CAT-01/03, NFR-09.
public sealed class CatalogIntegrationTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/";
    private static readonly string[] CatalogResources = ["product-types", "categories", "collections", "materials", "artisans"];
    public static TheoryData<string, int, int> BatchCases => new()
    {
        { "product-types", 0, 0 }, { "product-types", 1, 1 }, { "product-types", 200, 1 }, { "product-types", 201, 2 }, { "product-types", 350, 2 }, { "product-types", 401, 3 },
        { "materials", 0, 0 }, { "materials", 1, 1 }, { "materials", 200, 1 }, { "materials", 201, 2 }, { "materials", 350, 2 }, { "materials", 401, 3 },
        { "artisans", 0, 0 }, { "artisans", 1, 1 }, { "artisans", 200, 1 }, { "artisans", 201, 2 }, { "artisans", 350, 2 }, { "artisans", 401, 3 }
    };
    [Theory, MemberData(nameof(BatchCases))]
    public async Task EveryRenameEnqueuesAllIdsInCanonicalBatchesAndRedeliveryUsesLatestNames(string resource, int count, int batches)
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        var target = await SetupReferencesAsync(app, resource, count);
        var beforeSkus = await SkuFingerprintAsync(app);
        using var response = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + target.Id, new { target.RowVersion, name = "Tên lần một" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = app.OwnerContext();
        var messages = await db.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(batches, messages.Length);
        var expected = await db.Set<Product>().Select(x => x.Id).OrderBy(x => x).ToArrayAsync();
        var actual = messages.SelectMany(message =>
        {
            Assert.Equal(RebuildProductSearchText.Message, message.Type);
            var batch = JsonSerializer.Deserialize<RebuildProductSearchText>(message.Payload)!;
            Assert.InRange(batch.Ids.Length, 1, 200); return batch.Ids;
        }).ToArray();
        Assert.Equal(expected, actual.Order().ToArray()); Assert.Equal(count, actual.Distinct().Count()); Assert.Equal(count, actual.Length);
        // Persist newer names before consuming the old events; never trust payload names.
        await app.ExecuteAsync("UPDATE catalog.product_types SET name='Loại mới nhất'; UPDATE catalog.materials SET name='Chất liệu mới nhất'; UPDATE catalog.artisans SET name='Nghệ nhân mới nhất'");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.MessageType == RebuildProductSearchText.Message).ToArray();
            Assert.Single(handlers); Assert.IsType<RebuildProductSearchTextHandler>(handlers[0]);
            Assert.Equal(batches, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        }
        var search = await db.Set<Product>().AsNoTracking().OrderBy(x => x.Id).Select(x => x.SearchText).ToArrayAsync();
        Assert.All(search, text =>
        {
            Assert.Contains("loai moi nhat", text, StringComparison.Ordinal); Assert.Contains("chat lieu moi nhat", text, StringComparison.Ordinal);
            Assert.Contains("nghe nhan moi nhat", text, StringComparison.Ordinal); Assert.Contains("bien the", text, StringComparison.Ordinal);
        });
        // At-least-once replay of every batch must be safe and complete.
        await app.ExecuteAsync("UPDATE platform.outbox_messages SET status='pending' WHERE type='RebuildProductSearchText'");
        await using (var scope = app.Factory.Services.CreateAsyncScope()) Assert.Equal(batches, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        Assert.Equal(search, await db.Set<Product>().AsNoTracking().OrderBy(x => x.Id).Select(x => x.SearchText).ToArrayAsync());
        Assert.Equal(beforeSkus, await SkuFingerprintAsync(app));
        Assert.Equal(batches, await db.OutboxMessages.CountAsync(x => x.Status == "processed"));
    }
    [Theory]
    [InlineData("product-types")] [InlineData("materials")] [InlineData("artisans")]
    public async Task SecondBatchInsertFailureRollsBackTheWholeRenameAndAuditThenRetryProcessesEveryId(string resource)
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        var target = await SetupReferencesAsync(app, resource, 401);
        var before = await FingerprintAsync(app);
        await app.ExecuteAsync("""
CREATE SEQUENCE platform.integration_enqueue_attempts;
GRANT USAGE, SELECT ON SEQUENCE platform.integration_enqueue_attempts TO tp_app;
CREATE FUNCTION platform.integration_reject_second() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN IF NEW.type='RebuildProductSearchText' AND nextval('platform.integration_enqueue_attempts')=2 THEN RAISE EXCEPTION 'second batch fixture failure'; END IF; RETURN NEW; END $$;
CREATE TRIGGER integration_reject_second BEFORE INSERT ON platform.outbox_messages FOR EACH ROW EXECUTE FUNCTION platform.integration_reject_second();
""");
        using (var failed = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + target.Id, new { target.RowVersion, name = "Must roll back" })) Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await FingerprintAsync(app));
        await using (var db = app.OwnerContext())
        {
            Assert.Empty(await db.OutboxMessages.ToArrayAsync());
            Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT last_value AS \"Value\" FROM platform.integration_enqueue_attempts").SingleAsync());
        }
        Assert.Equal(target.RowVersion, (await app.Client.GetFromJsonAsync<JsonElement>(Root + resource + "/" + target.Id)).GetProperty("rowVersion").GetString());
        await app.ExecuteAsync("DROP TRIGGER integration_reject_second ON platform.outbox_messages; DROP FUNCTION platform.integration_reject_second(); DROP SEQUENCE platform.integration_enqueue_attempts");
        using var retry = await app.MutateAsync(HttpMethod.Patch, Root + resource + "/" + target.Id, new { target.RowVersion, name = "Retry thành công" }); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var check = app.OwnerContext(); Assert.Equal(3, await check.OutboxMessages.CountAsync());
        await using var scope = app.Factory.Services.CreateAsyncScope(); Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        Assert.Equal(401, await check.Set<Product>().CountAsync(x => x.SearchText.Contains("retry thanh cong")));
    }

    [Fact]
    public async Task SeedThenMaterialsCrudAndRepeatSeedPreserveIdsCodesMetadataAndPublicationHistory()
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        var seeded = await SeedAsync(app); Assert.True(seeded.IsSuccess); Assert.Equal(new CatalogSeedOutcome(9, 23, 14, 13), seeded.Value);
        await using (var db = app.OwnerContext())
        {
            var materials = await db.Set<Material>().AsNoTracking().ToArrayAsync();
            Assert.Equal(13, materials.Length); Assert.All(materials, m => { Assert.Equal("draft", m.Status); Assert.Null(m.FirstPublishedAt); });
            foreach (var reference in CatalogReferenceDefaults.Materials())
            {
                var material = Assert.Single(materials, m => m.Id == reference.Id);
                Assert.Equal(reference.SkuCode, material.SkuCode); Assert.Equal(reference.Slug, material.Slug); Assert.Equal(reference.Kind, material.Kind);
            }
        }
        var referenceId = CatalogReferenceDefaults.Materials()[0].Id;
        var original = (await app.Client.GetFromJsonAsync<CatalogView>(Root + "materials/" + referenceId))!;
        using var patch = await app.MutateAsync(HttpMethod.Patch, Root + "materials/" + referenceId, new { original.RowVersion, name = "Tên owner", summary = "Metadata owner", descriptionHtml = "<div>nội dung</div><div>owner</div>", seoTitle = "SEO owner" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode); var edited = (await patch.Content.ReadFromJsonAsync<CatalogView>())!;
        var beforeRepeat = await FingerprintAsync(app); var repeated = await SeedAsync(app); Assert.True(repeated.IsSuccess); Assert.Equal(new CatalogSeedOutcome(0, 0, 0, 0), repeated.Value);
        Assert.Equal(beforeRepeat, await FingerprintAsync(app)); Assert.Equal(edited, await app.Client.GetFromJsonAsync<CatalogView>(Root + "materials/" + referenceId));
        var seededTypeId = CatalogReferenceDefaults.Types()[0].Id;
        await app.ExecuteAsync($$"""
INSERT INTO catalog.products(id,product_type_id,material_id,sku_base,slug,name) VALUES ('00000000-0000-0000-0000-000000000777','{{seededTypeId}}','{{referenceId}}','AM-TN-0777','seeded-reference-product','Fixture product');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd) VALUES ('00000000-0000-0000-0000-000000000778','00000000-0000-0000-0000-000000000777','AM-TN-0777',1000);
""");
        var assignedSkus = await SkuFingerprintAsync(app);
        using var published = await app.MutateAsync(HttpMethod.Post, Root + "materials/" + referenceId + "/publish", new { edited.RowVersion }); Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var current = (await app.Client.GetFromJsonAsync<CatalogView>(Root + "materials/" + referenceId))!; Assert.NotNull(current.FirstPublishedAt); Assert.Equal(original.SkuCode, current.SkuCode);
        var beforePublishedRepeat = await FingerprintAsync(app); Assert.True((await SeedAsync(app)).IsSuccess); Assert.Equal(beforePublishedRepeat, await FingerprintAsync(app));
        Assert.Equal(current.FirstPublishedAt, (await app.Client.GetFromJsonAsync<CatalogView>(Root + "materials/" + referenceId))!.FirstPublishedAt);
        using var delete = await app.MutateAsync(HttpMethod.Delete, Root + "materials/" + referenceId, new { }); Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(assignedSkus, await SkuFingerprintAsync(app));
        await using var referencedDb = app.OwnerContext(); Assert.Equal(referenceId, (await referencedDb.Set<Product>().SingleAsync()).MaterialId);
    }

    [Fact]
    public async Task SeedAfterAdminDataKeepsTheCustomMaterialAndRejectsConflictingIdentityAtomically()
    {
        await using var app = await P009Harness.CreateAsync(fixture); await app.LoginAsync();
        using var created = await app.MutateAsync(HttpMethod.Post, Root + "materials", new { name = "Custom wood", kind = "wood", skuCode = "CW", summary = "Keep this" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var custom = (await created.Content.ReadFromJsonAsync<CatalogView>())!;
        Assert.True((await SeedAsync(app)).IsSuccess); Assert.True((await SeedAsync(app)).IsSuccess);
        Assert.Equal(custom, await app.Client.GetFromJsonAsync<CatalogView>(Root + "materials/" + custom.Id));
        await using var db = app.OwnerContext(); Assert.Equal(14, await db.Set<Material>().CountAsync());
        // A second empty target with Admin-created default code but a different ID must fail safely.
        await using var conflictApp = await P009Harness.CreateAsync(fixture); await conflictApp.LoginAsync();
        using var conflicting = await conflictApp.MutateAsync(HttpMethod.Post, Root + "materials", new { name = "Owner TN", kind = "clay", skuCode = "TN", slug = "owner-tn", summary = "Prior value" });
        Assert.Equal(HttpStatusCode.Created, conflicting.StatusCode); var before = await FingerprintAsync(conflictApp);
        var rejected = await SeedAsync(conflictApp); Assert.False(rejected.IsSuccess); Assert.Equal("catalog_seed_identity_conflict", rejected.Error.Code);
        Assert.Equal(before, await FingerprintAsync(conflictApp));
        await using var conflictDb = conflictApp.OwnerContext(); Assert.Single(await conflictDb.Set<Material>().ToArrayAsync()); Assert.Empty(await conflictDb.Set<ProductType>().ToArrayAsync());
    }

    [Fact]
    public async Task CombinedOpenApiAndDiExposeExactlyThirtyTwoCatalogOperationsAndOneRebuildHandler()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient(); using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Where(p => CatalogResources.Any(resource => p.Name.StartsWith(Root + resource, StringComparison.Ordinal))).ToArray();
        Assert.Equal(17, paths.Length); Assert.Equal(32, paths.Sum(p => p.Value.EnumerateObject().Count(o => o.Name is "get" or "post" or "patch" or "put")));
        Assert.All(paths, path => Assert.False(path.Value.TryGetProperty("delete", out _)));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("ProductTypePatch", out _)); Assert.True(schemas.TryGetProperty("CatalogPublication", out _));
        await using var app = await P009Harness.CreateAsync(fixture);
        await using var scope = app.Factory.Services.CreateAsyncScope(); Assert.Single(scope.ServiceProvider.GetServices<IOutboxHandler>(), h => h.MessageType == RebuildProductSearchText.Message);
    }

    private static async Task<(Guid Id, string RowVersion)> SetupReferencesAsync(P009Harness app, string resource, int count)
    {
        using var typeResponse = await app.MutateAsync(HttpMethod.Post, Root + "product-types", new { code = "integration_type", name = "Loại ban đầu", skuPrefix = "IT" });
        Assert.Equal(HttpStatusCode.Created, typeResponse.StatusCode); var type = (await typeResponse.Content.ReadFromJsonAsync<ProductTypeView>())!;
        using var materialResponse = await app.MutateAsync(HttpMethod.Post, Root + "materials", new { name = "Chất liệu ban đầu", kind = "clay", skuCode = "TN" });
        Assert.Equal(HttpStatusCode.Created, materialResponse.StatusCode); var material = (await materialResponse.Content.ReadFromJsonAsync<CatalogView>())!;
        using var artisanResponse = await app.MutateAsync(HttpMethod.Post, Root + "artisans", new { name = "Nghệ nhân ban đầu" });
        Assert.Equal(HttpStatusCode.Created, artisanResponse.StatusCode); var artisan = (await artisanResponse.Content.ReadFromJsonAsync<CatalogView>())!;
        // Generated synthetic records exercise references without adding any product CRUD API.
        await app.ExecuteAsync($$"""
INSERT INTO catalog.products(id,product_type_id,material_id,artisan_id,sku_base,slug,name)
SELECT md5('integration-product-'||i)::uuid, '{{type.Id}}', '{{material.Id}}', '{{artisan.Id}}', 'IT-TN-'||lpad(i::text,4,'0'), 'integration-'||i, 'Sản phẩm '||i FROM generate_series(1,{{count}}) i;
INSERT INTO catalog.variants(id,product_id,sku,name,price_vnd)
SELECT md5('integration-variant-'||i)::uuid,md5('integration-product-'||i)::uuid,'IT-TN-'||lpad(i::text,4,'0'),'Biến thể '||i,1000 FROM generate_series(1,{{count}}) i;
""");
        return resource switch { "product-types" => (type.Id, type.RowVersion), "materials" => (material.Id, material.RowVersion), _ => (artisan.Id, artisan.RowVersion) };
    }
    private static async Task<Result<CatalogSeedOutcome>> SeedAsync(P009Harness app)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedCatalogReference, CatalogSeedOutcome>>().Handle(new(), default);
    }
    private static async Task<string> SkuFingerprintAsync(P009Harness app)
    {
        await using var db = app.OwnerContext();
        return await db.Database.SqlQueryRaw<string>("""
SELECT md5(concat((SELECT jsonb_agg(jsonb_build_array(id,sku_base) ORDER BY id) FROM catalog.products),
 (SELECT jsonb_agg(jsonb_build_array(id,sku) ORDER BY id) FROM catalog.variants))) AS "Value"
""").SingleAsync();
    }
    private static async Task<string> FingerprintAsync(P009Harness app)
    {
        await using var db = app.OwnerContext();
        return await db.Database.SqlQueryRaw<string>("""
SELECT md5(concat(
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.product_types t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.attribute_definitions t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.attribute_options t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.materials t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.artisans t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.products t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM catalog.variants t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.outbox_messages t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.audit_logs t))) AS "Value"
""").SingleAsync();
    }
}
