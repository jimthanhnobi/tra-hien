using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class ProductVariantTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task MissingReferenceCodesAreRejectedAndOnlyAnUnselectedMaterialUsesXx()
    {
        await using var h = await ProductHarness.Create(fixture);
        await h.App.ExecuteAsync($"UPDATE catalog.product_types SET sku_prefix=NULL WHERE id='{h.Type}'");
        using var prefix = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, new { name = "Missing prefix", productTypeId = h.Type });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, prefix.StatusCode); Assert.Contains("sku_prefix_missing", await prefix.Content.ReadAsStringAsync());
        await h.App.ExecuteAsync($"UPDATE catalog.product_types SET sku_prefix='AM' WHERE id='{h.Type}'; UPDATE catalog.materials SET sku_code=NULL WHERE id='{h.Material}'");
        using var code = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, new { name = "Missing material code", productTypeId = h.Type, materialId = h.Material });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, code.StatusCode); Assert.Contains("material_sku_code_missing", await code.Content.ReadAsStringAsync());
        var p = await h.Create(initial: new { name = "No material", productTypeId = h.Type }); Assert.Equal("AM-XX-0001", p.SkuBase);
    }
    [Fact]
    public async Task FirstAndConcurrentAdditionalSkusStayUniqueAndEditsNeverRegenerate()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var first = p.Variants[0].Sku;
        // Independent HTTP requests with a shared parent version: parent lock and stale-version rejection prevent lost child writes.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/variants", new { p.RowVersion, name = "same axis" })));
        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses) response.Dispose();
        p = await h.Get(p.Id); Assert.Equal(2, p.Variants.Count); Assert.Equal(first, p.Variants[0].Sku); Assert.Equal(2, p.Variants.Select(x => x.Sku).Distinct().Count());
        var v = p.Variants[1]; var oldSku = v.Sku;
        p = await h.Change(p, "/variants/" + v.Id, new { name = "renamed", attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) }, status = "inactive" }, HttpMethod.Patch, v.RowVersion);
        Assert.Equal(oldSku, p.Variants.Single(x => x.Id == v.Id).Sku);
        p = await h.Patch(p, new { materialId = (Guid?)null, productTypeId = h.OtherType, confirmTypeChange = true });
        Assert.Equal(first, p.SkuBase); Assert.Equal(oldSku, p.Variants.Single(x => x.Id == v.Id).Sku);
    }
    [Fact]
    public async Task ConcurrentProductsSequenceBoundaryAndRollbackGapsUseNextval()
    {
        await using var h = await ProductHarness.Create(fixture);
        await h.App.ExecuteAsync("SELECT setval('catalog.sku_number_seq',9998,true)");
        var first = await h.Create("First boundary"); var second = await h.Create("Second boundary");
        Assert.EndsWith("9999", first.SkuBase); Assert.EndsWith("10000", second.SkuBase);
        var products = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => h.Create("Concurrent " + i)));
        Assert.Equal(8, products.Select(x => x.SkuBase).Distinct().Count());
        await h.App.ExecuteAsync("""
CREATE FUNCTION catalog.reject_product_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture'; END $$;
CREATE TRIGGER reject_product_fixture BEFORE INSERT ON catalog.products FOR EACH ROW EXECUTE FUNCTION catalog.reject_product_fixture();
""");
        using var failed = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, new { name = "Rollback sequence", productTypeId = h.Type }); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await h.App.ExecuteAsync("DROP TRIGGER reject_product_fixture ON catalog.products; DROP FUNCTION catalog.reject_product_fixture()");
        var next = await h.Create("After rollback"); Assert.EndsWith("10010", next.SkuBase);
    }
    [Fact]
    public async Task GeneratedBaseCollisionUsesSavepointAndManualInactiveSkusRemainReserved()
    {
        await using var h = await ProductHarness.Create(fixture); var first = await h.Create(); var v = first.Variants[0];
        first = await h.Change(first, "/variants/" + v.Id, new { sku = "AM-TN-0002", status = "inactive" }, HttpMethod.Patch, v.RowVersion);
        var second = await h.Create("Colliding generated base"); Assert.Equal("AM-TN-0002", second.SkuBase); Assert.Equal("AM-TN-0002-V2", second.Variants[0].Sku);
        await using (var db = h.App.OwnerContext())
        {
            var logs = await db.AuditLogs.Where(x => x.EntityType == "catalog.variants" && x.EntityId == second.Variants[0].Id).ToArrayAsync(); Assert.Single(logs);
            Assert.Equal(2, await db.Set<Variant>().CountAsync()); Assert.Equal(3, await db.OutboxMessages.CountAsync(x => x.Type == "StorefrontInvalidate"));
        }
        using var duplicate = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + second.Id + "/variants/" + second.Variants[0].Id,
            new { second.Variants[0].RowVersion, sku = "AM-TN-0002" }); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(second.RowVersion, (await h.Get(second.Id)).RowVersion);
    }
    [Fact]
    public async Task SuffixExhaustionRollsBackParentAndAuditButKeepsTheSequenceGap()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        var skus = Enumerable.Range(0, ProductRules.SkuAttempts).Select(i => ProductRules.SkuCandidate("AM-TN-0002", true, null, 1, i)).ToArray();
        await using (var db = h.App.OwnerContext())
        {
            foreach (var sku in skus) db.Add(new Variant { Id = Guid.CreateVersion7(), ProductId = p.Id, Sku = sku, Status = "inactive" });
            await db.SaveChangesAsync();
        }
        using var response = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, new { name = "Exhausted", productTypeId = h.Type, materialId = h.Material });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("sku_allocation_exhausted", await response.Content.ReadAsStringAsync());
        await using (var db = h.App.OwnerContext()) Assert.Equal(1, await db.Set<Product>().CountAsync());
        Assert.EndsWith("0003", (await h.Create("After exhaustion")).SkuBase);
    }
    [Fact]
    public async Task MovementsAndOrderUsageContractBothProtectOwnerManualSkuEdits()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var v = p.Variants[0];
        await h.App.ExecuteAsync($"INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,on_hand_after,actor_type,occurred_at) VALUES ('{Guid.CreateVersion7()}','{v.Id}','opening',1,1,'system',now())");
        using var response = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, sku = "MANUAL-NEW" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("sku_locked", await response.Content.ReadAsStringAsync());
        await using var order = await ProductHarness.Create(fixture, services => services.AddScoped<IVariantUsage, OrderUsage>());
        var other = await order.Create(); var ov = other.Variants[0];
        using var orderResponse = await order.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + other.Id + "/variants/" + ov.Id, new { ov.RowVersion, sku = "ORDER-NEW" });
        Assert.Equal(HttpStatusCode.Conflict, orderResponse.StatusCode); Assert.Contains("sku_locked", await orderResponse.Content.ReadAsStringAsync());
    }
    private sealed class OrderUsage : IVariantUsage
    { public Task<bool> UsedAsync(Guid variantId, CancellationToken ct) => Task.FromResult(true); }
}
