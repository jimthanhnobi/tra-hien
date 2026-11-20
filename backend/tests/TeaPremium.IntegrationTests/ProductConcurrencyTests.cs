using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class ProductConcurrencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task ReciprocalRelatedProductWritesDoNotDeadlockOnForeignKeyKeyShare()
    {
        var barrier = new ParentBarrier();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(barrier)));
        var a = await h.Create("Related A"); var b = await h.Create("Related B"); barrier.Enabled = true;
        var results = await Task.WhenAll(
            h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + a.Id, new { a.RowVersion, relatedProductIds = new[] { b.Id } }),
            h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + b.Id, new { b.RowVersion, relatedProductIds = new[] { a.Id } })).WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var response in results) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        Assert.Equal(b.Id, Assert.Single((await h.Get(a.Id)).RelatedProductIds)); Assert.Equal(a.Id, Assert.Single((await h.Get(b.Id)).RelatedProductIds));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealProductAndPublishedMaterialHttpWritesRespectBothLockOrders(bool productFirst)
    {
        var gate = new CommandGate("pg_advisory_xact_lock(8104, 100)", !productFirst);
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var p = await ProductPublicationTests.Ready(h, await h.Create(), 150); p = await h.Change(p, "/publish", new { });
        await h.App.ExecuteAsync($"UPDATE catalog.materials SET status='published',first_published_at=now() WHERE id='{h.Material}'; INSERT INTO content.redirects(source_path,target_path) VALUES ('/trang/inbound','/san-pham/{p.Slug}')");
        var material = (await h.App.Client.GetFromJsonAsync<CatalogView>("/api/v1/admin/materials/" + h.Material))!;
        Task<HttpResponseMessage> ProductWrite() => h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, slug = "concurrent-new" });
        Task<HttpResponseMessage> MaterialWrite() => h.App.MutateAsync(HttpMethod.Patch, "/api/v1/admin/materials/" + h.Material, new { material.RowVersion, name = "Material renamed" });
        gate.Enabled = true; var first = productFirst ? ProductWrite() : MaterialWrite();
        Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); second = productFirst ? MaterialWrite() : ProductWrite();
            await ProductLockOrderTests.WaitForLock(h.App.OwnerConnection, "catalog.materials", productFirst ? "RowExclusiveLock" : "ShareLock");
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        using var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
        Assert.NotNull(second); using var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(HttpStatusCode.OK, secondResult.StatusCode);
        await using var db = h.App.OwnerContext(); var graph = await db.Set<Redirect>().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath);
        Assert.Equal("/san-pham/concurrent-new", graph["/trang/inbound"]); Assert.All(graph, pair => Assert.False(graph.ContainsKey(pair.Value)));
        Assert.Equal("Material renamed", (await db.Set<Material>().SingleAsync()).Name); Assert.Equal("concurrent-new", (await db.Set<Product>().SingleAsync()).Slug);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentLockPreventsPublishRacingWithNullPriceInBothOrders(bool publishFirst)
    {
        var gate = new CommandGate("FROM catalog.products WHERE id=", true, reader: true);
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var p = await ProductPublicationTests.Ready(h, await h.Create(), 150); var v = p.Variants[0];
        Task<HttpResponseMessage> Publish() => h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/publish", new { p.RowVersion });
        Task<HttpResponseMessage> RemovePrice() => h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, priceVnd = (long?)null });
        gate.Enabled = true; var first = publishFirst ? Publish() : RemovePrice(); Task<HttpResponseMessage>? second = null;
        try { await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); second = publishFirst ? RemovePrice() : Publish(); await Task.Delay(100); Assert.False(second.IsCompleted); }
        finally { gate.Release.TrySetResult(); }
        using var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.NotNull(second); using var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(publishFirst ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.Conflict, r2.StatusCode);
        var saved = await h.Get(p.Id); Assert.True(saved.Status != "active" || saved.Variants.Where(x => x.Status == "active").All(x => ProductRules.ValidPrice(x.PriceVnd)));
    }
    [Theory]
    [InlineData(true, "product")]
    [InlineData(false, "product")]
    [InlineData(true, "variant")]
    [InlineData(false, "variant")]
    public async Task ReferenceMetadataAndRealValueWriterRespectTheP103Contract(bool writerFirst, string scope)
    {
        var gate = new CommandGate(writerFirst ? "catalog.materials IN SHARE MODE" : "catalog.materials IN SHARE ROW EXCLUSIVE MODE", true);
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var p = await h.Create(); var attributeId = scope == "product" ? h.ProductAttribute : h.VariantAttribute;
        var attrPath = $"/api/v1/admin/product-types/{h.Type}/attributes/{attributeId}";
        var attr = (await h.App.Client.GetFromJsonAsync<AttributeView>(attrPath))!;
        Task<HttpResponseMessage> Writer() => h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + (scope == "product" ? "" : "/variants/" + p.Variants[0].Id),
            new { rowVersion = scope == "product" ? p.RowVersion : p.Variants[0].RowVersion, attributes = new[] { new ProductValue(attributeId, ValueNumber: 180) } });
        Task<HttpResponseMessage> Metadata() => h.App.MutateAsync(HttpMethod.Patch, attrPath, new { attr.RowVersion, unit = "ml" });
        gate.Enabled = true; var first = writerFirst ? Writer() : Metadata(); Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); second = writerFirst ? Metadata() : Writer();
            await ProductLockOrderTests.WaitForLock(h.App.OwnerConnection, "catalog.product_types", writerFirst ? "ShareRowExclusiveLock" : "ShareLock"); Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        using var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.NotNull(second); using var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(writerFirst ? HttpStatusCode.Conflict : HttpStatusCode.OK, r2.StatusCode);
        var persisted = (await h.App.Client.GetFromJsonAsync<AttributeView>(attrPath))!;
        Assert.Equal(writerFirst ? null : "ml", persisted.Unit); var saved = await h.Get(p.Id);
        Assert.Equal(180, Assert.Single(scope == "product" ? saved.Attributes : saved.Variants[0].Attributes).ValueNumber);
    }
    private sealed class ParentBarrier : DbCommandInterceptor
    {
        public volatile bool Enabled;
        private int count;
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM catalog.products WHERE id=", StringComparison.Ordinal) && command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal))
            { if (Interlocked.Increment(ref count) == 2) both.TrySetResult(); await both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
    private sealed class CommandGate(string match, bool after, bool reader = false) : DbCommandInterceptor
    {
        public volatile bool Enabled;
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Wait(DbCommand command, CancellationToken ct)
        {
            if (Enabled && command.CommandText.Contains(match, StringComparison.Ordinal) && Interlocked.CompareExchange(ref entered, 1, 0) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (!reader && !after) await Wait(command, cancellationToken); return result; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (!reader && after) await Wait(command, cancellationToken); return result; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        { if (reader) await Wait(command, cancellationToken); return result; }
    }
}
