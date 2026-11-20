using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class ProductOutboxContractTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task CategoryInvalidationBatchesCoverEveryTagWithoutExceedingTheConsumerLimit()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToArray();
        await h.App.ExecuteAsync("INSERT INTO catalog.categories(id,slug,name,status,first_published_at) VALUES " + string.Join(',', ids.Select((id, i) => $"('{id}','batch-{i}','Fixture','visible',now())")));
        p = await h.Patch(p, new { categoryIds = ids });
        await using var db = h.App.OwnerContext(); var payloads = await db.OutboxMessages.Where(x => x.Type == StorefrontInvalidate.Message).OrderBy(x => x.Id).Select(x => x.Payload).ToArrayAsync();
        var latest = payloads.Skip(1).Select(x => JsonSerializer.Deserialize<StorefrontInvalidate>(x)!).ToArray(); Assert.Equal(2, latest.Length);
        Assert.All(latest, x => Assert.InRange(x.Tags.Length, 1, 200));
        var tags = latest.SelectMany(x => x.Tags).ToArray(); Assert.Equal(201, tags.Count(x => x.StartsWith("category:", StringComparison.Ordinal))); Assert.Equal(tags.Length, tags.Distinct().Count());
    }
    [Fact]
    public async Task SearchWritesAndCanonicalRenameReplayReadCurrentNamesAndPreserveSku()
    {
        var receiver = new Receiver(HttpStatusCode.OK);
        await using var h = await ProductHarness.Create(fixture, services => services.AddHttpClient<StorefrontInvalidateHandler>().ConfigurePrimaryHttpMessageHandler(() => receiver));
        h.App.Configuration["STORE_REVALIDATE_URL"] = "http://store.invalid/api/revalidate"; h.App.Configuration["REVALIDATE_SECRET"] = "test-only-revalidate";
        var p = await h.Create("Ấm tử sa"); var v = p.Variants[0];
        p = await h.Change(p, "/variants/" + v.Id, new { name = "Dung tích 245ml" }, HttpMethod.Patch, v.RowVersion);
        Assert.Contains("am tu sa", p.SearchText); Assert.Contains("245ml", p.SearchText); Assert.Contains(v.Sku.ToLowerInvariant(), p.SearchText);
        var typePath = "/api/v1/admin/product-types/" + h.Type;
        var type = (await h.App.Client.GetFromJsonAsync<ProductTypeView>(typePath))!;
        using var rename = await h.App.MutateAsync(HttpMethod.Patch, typePath, new { type.RowVersion, name = "Loại mới" }); Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        await h.App.ExecuteAsync($"UPDATE catalog.product_types SET name='Tên hiện hành' WHERE id='{h.Type}'");
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            Assert.Single(scope.ServiceProvider.GetServices<IOutboxHandler>(), x => x.MessageType == RebuildProductSearchText.Message);
            Assert.Single(scope.ServiceProvider.GetServices<IOutboxHandler>(), x => x.MessageType == StorefrontInvalidate.Message);
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(20, default);
            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(x => x.MessageType == RebuildProductSearchText.Message);
            await handler.HandleAsync(Guid.CreateVersion7(), JsonSerializer.Serialize(new RebuildProductSearchText([p.Id])), default);
        }
        var saved = await h.Get(p.Id); Assert.Contains("ten hien hanh", saved.SearchText); Assert.Equal(v.Sku, saved.Variants[0].Sku);
        Assert.NotEmpty(receiver.Payloads); Assert.All(receiver.Payloads, payload => { Assert.Contains("catalog", payload); Assert.Contains("sitemap", payload); Assert.Contains("feed", payload); });
    }
    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 503)]
    public async Task MissingReceiverConfigurationOrDeliveryFailureNeverAcknowledgesInvalidation(bool configured, int status)
    {
        var receiver = new Receiver((HttpStatusCode)status);
        await using var h = await ProductHarness.Create(fixture, services => services.AddHttpClient<StorefrontInvalidateHandler>().ConfigurePrimaryHttpMessageHandler(() => receiver));
        if (configured) { h.App.Configuration["STORE_REVALIDATE_URL"] = "http://store.invalid/api/revalidate"; h.App.Configuration["REVALIDATE_SECRET"] = "test-secret-no-log"; }
        await h.Create();
        await using (var scope = h.App.Factory.Services.CreateAsyncScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        await using var db = h.App.OwnerContext(); var message = await db.OutboxMessages.SingleAsync(x => x.Type == StorefrontInvalidate.Message);
        Assert.Equal("pending", message.Status); Assert.Equal(1, message.Attempts); Assert.Null(message.ProcessedAt); Assert.Equal("handler_failed", message.LastError);
        Assert.DoesNotContain("test-secret", message.Payload); Assert.Equal(configured ? 1 : 0, receiver.Payloads.Count);
    }
    [Theory]
    [InlineData("outbox")]
    [InlineData("audit")]
    [InlineData("redirect")]
    public async Task FailureAfterEarlySavesRollsBackAggregateVersionsAuditOutboxAndRedirects(string failedStage)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await ProductPublicationTests.Ready(h, await h.Create(), 150); p = await h.Change(p, "/publish", new { });
        var before = await Fingerprint(h);
        var table = failedStage switch { "outbox" => "platform.outbox_messages", "audit" => "platform.audit_logs", _ => "content.redirects" };
        await h.App.ExecuteAsync($"CREATE FUNCTION catalog.reject_stage_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-fixture-failure'; END $$; CREATE TRIGGER reject_stage_fixture BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION catalog.reject_stage_fixture()");
        using var result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, slug = "transaction-new", descriptionHtml = ProductPublicationTests.Words(149) });
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode); Assert.DoesNotContain("private-fixture-failure", await result.Content.ReadAsStringAsync());
        Assert.Equal(before, await Fingerprint(h));
        await h.App.ExecuteAsync($"DROP TRIGGER reject_stage_fixture ON {table}; DROP FUNCTION catalog.reject_stage_fixture()");
        var retried = await h.Patch(p, new { slug = "transaction-new", descriptionHtml = ProductPublicationTests.Words(149) }); Assert.Equal("transaction-new", retried.Slug);
    }
    [Fact]
    public async Task OpenApiAddsExactlyElevenProductPathsAndSixteenOperationsAndKeepsReferenceTaxonomyContracts()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient(); using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var all = document.RootElement.GetProperty("paths");
        var paths = all.EnumerateObject().Where(p => p.Name.StartsWith(ProductApiTests.Root, StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, paths.Length); Assert.Equal(16, paths.Sum(p => p.Value.EnumerateObject().Count(o => o.Name is "get" or "post" or "patch" or "put" or "delete")));
        var baselinePath = Path.Combine(FindRoot(), "packages", "api-client", "openapi.json"); using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        foreach (var old in baseline.RootElement.GetProperty("paths").EnumerateObject())
            if (!old.Name.StartsWith(ProductApiTests.Root, StringComparison.Ordinal)) Assert.True(JsonElement.DeepEquals(old.Value, all.GetProperty(old.Name)), old.Name);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas"); Assert.True(schemas.TryGetProperty("ProductData", out _)); Assert.True(schemas.TryGetProperty("VariantWriteRequest", out _));
        await using var h = await ProductHarness.Create(fixture); await using var db = h.App.OwnerContext(); Assert.False(db.Database.HasPendingModelChanges());
    }
    private static string FindRoot()
    { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pnpm-workspace.yaml"))) dir = dir.Parent; return dir?.FullName ?? throw new InvalidOperationException("Repository missing."); }
    private static async Task<string> Fingerprint(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        return JsonSerializer.Serialize(new
        {
            products = await db.Set<Product>().OrderBy(x => x.Id).ToArrayAsync(),
            variants = await db.Set<Variant>().OrderBy(x => x.Id).ToArrayAsync(),
            audit = await db.AuditLogs.OrderBy(x => x.Id).ToArrayAsync(),
            outbox = await db.OutboxMessages.OrderBy(x => x.Id).ToArrayAsync(),
            redirects = await db.Set<Redirect>().OrderBy(x => x.SourcePath).ToArrayAsync()
        });
    }
    private sealed class Receiver(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.True(request.Headers.Contains("X-Revalidate-Secret"));
            Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); return new(status);
        }
    }
}
