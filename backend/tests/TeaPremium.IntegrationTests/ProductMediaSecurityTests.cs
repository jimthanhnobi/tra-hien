using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Media;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class ProductMediaSecurityTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task CompletedP102PublicAssetMediaOwnershipOrderCoverPatchAndDeleteUseExistingAssets()
    {
        var storage = new FakeMediaStorage();
        await using var h = await ProductHarness.Create(fixture, services =>
        {
            services.RemoveAll<IMediaStorage>(); services.AddSingleton<IMediaStorage>(storage);
            services.RemoveAll<MediaStorageOptions>(); services.AddSingleton(new MediaStorageOptions { PublicBaseUrl = "https://public.fake.invalid", FfmpegPath = MediaContentTests.Ffmpeg, FfprobePath = MediaContentTests.Ffprobe });
        });
        var p = await h.Create(); var other = await h.Create("Other product"); var bytes = MediaContentTests.Bytes("png");
        using var intentResponse = await h.App.MutateAsync(HttpMethod.Post, "/api/v1/admin/media/upload-intents", new { fileName = "fixture.png", mimeType = "image/png", byteSize = bytes.Length, purpose = "catalog", slugHint = "fixture-product" });
        Assert.Equal(HttpStatusCode.OK, intentResponse.StatusCode); var intent = (await intentResponse.Content.ReadFromJsonAsync<UploadIntent>())!;
        using var pending = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media", new { p.RowVersion, assetId = intent.UploadId, altText = "Ảnh ấm" }); Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        await using (var db = h.App.OwnerContext()) { var session = await db.Set<UploadSession>().SingleAsync(); storage.Staging[MediaKeys.Staging(session)] = new(bytes, "image/png"); }
        using var complete = await h.App.MutateAsync(HttpMethod.Post, "/api/v1/admin/media/upload-intents/" + intent.UploadId + "/complete", new { }); Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var asset = (await complete.Content.ReadFromJsonAsync<AssetView>())!;
        foreach (var alt in new[] { "", "ab", new string('a', 201) })
        { using var invalid = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media", new { p.RowVersion, assetId = asset.Id, altText = alt }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using var cross = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media", new { p.RowVersion, assetId = asset.Id, altText = "Ảnh ấm", variantId = other.Variants[0].Id }); Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
        p = await h.Change(p, "/media", new { assetId = asset.Id, altText = "Ảnh ấm", variantId = p.Variants[0].Id }); Assert.Equal(asset.Url, p.Media[0].Url);
        var image = p.Media[0];
        p = await h.Change(p, "/media/" + image.Id, new { altText = "ALT mới", variantId = (Guid?)null }, HttpMethod.Patch); Assert.Null(p.Media[0].VariantId);
        p = await h.Change(p, "/media/order", new { mediaIds = new[] { image.Id } }, HttpMethod.Put);
        p = await h.Change(p, "/media/" + image.Id + "/cover", new { }); Assert.True(Assert.Single(p.Media).IsCover);
        using var variants = await h.App.Client.GetAsync(ProductApiTests.Root + "/" + p.Id + "/variants"); Assert.Equal(HttpStatusCode.OK, variants.StatusCode);
        using var variant = await h.App.Client.GetAsync(ProductApiTests.Root + "/" + p.Id + "/variants/" + p.Variants[0].Id); Assert.Equal(HttpStatusCode.OK, variant.StatusCode);
        p = await h.Change(p, "/media/" + image.Id, new { }, HttpMethod.Delete); Assert.Empty(p.Media);
        await using var final = h.App.OwnerContext(); Assert.Equal(1, await final.Set<MediaAsset>().CountAsync()); Assert.Empty(await final.Set<StockLevel>().ToArrayAsync());
    }
    [Fact]
    public async Task PrivateAndVideoAssetsCannotAttachOrBecomeOgImages()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        foreach (var mime in new[] { "image/png", "video/mp4" })
        {
            var id = Guid.CreateVersion7(); await h.App.ExecuteAsync($"INSERT INTO media.assets(id,storage_key,mime_type,byte_size,purpose) VALUES ('{id}','evidence/{id}','{mime}',1,'evidence')");
            using var attach = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media", new { p.RowVersion, assetId = id, altText = "Private evidence" }); Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
            using var og = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, ogMediaId = id }); Assert.Equal(HttpStatusCode.BadRequest, og.StatusCode);
        }
        Assert.Empty((await h.Get(p.Id)).Media);
    }
    [Fact]
    public async Task CoverReplacementRollbackAndConcurrentChangesPreserveExactlyOneCover()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await ProductPublicationTests.Ready(h, await h.Create(), 150); var original = p.Media[0].Id;
        var asset = Guid.CreateVersion7(); await h.App.ExecuteAsync($"INSERT INTO media.assets(id,storage_key,mime_type,byte_size) VALUES ('{asset}','catalog/{asset}','image/jpeg',1)");
        p = await h.Change(p, "/media", new { assetId = asset, altText = "Second image" }); var second = p.Media.Single(x => x.Id != original).Id;
        await h.App.ExecuteAsync("""
CREATE FUNCTION catalog.reject_cover_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.is_cover THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$;
CREATE TRIGGER reject_cover_fixture BEFORE UPDATE ON catalog.product_media FOR EACH ROW EXECUTE FUNCTION catalog.reject_cover_fixture();
""");
        using var failure = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media/" + second + "/cover", new { p.RowVersion }); Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var saved = await h.Get(p.Id); Assert.Equal(p.RowVersion, saved.RowVersion); Assert.Equal(original, Assert.Single(saved.Media, x => x.IsCover).Id);
        await h.App.ExecuteAsync("DROP TRIGGER reject_cover_fixture ON catalog.product_media; DROP FUNCTION catalog.reject_cover_fixture()");
        var results = await Task.WhenAll(new[] { original, second }.Select(id => h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/media/" + id + "/cover", new { p.RowVersion })));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict); foreach (var r in results) r.Dispose();
        Assert.Single((await h.Get(p.Id)).Media, x => x.IsCover);
    }
    [Fact]
    public async Task RealCookieCsrfPermissionsPricingAndFieldFilteringAreEnforced()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        using var noCsrf = await h.App.Client.PatchAsJsonAsync(ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, name = "Denied" }); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var content = await Staff(h, "content"); await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        var v = p.Variants[0];
        using var price = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, priceVnd = 1000 }); Assert.Equal(HttpStatusCode.Forbidden, price.StatusCode);
        using var sku = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, sku = "MANUAL-1" }); Assert.Equal(HttpStatusCode.Forbidden, sku.StatusCode);
        using var low = await h.App.Client.GetAsync(ProductApiTests.Root + "?stock=low"); Assert.Equal(HttpStatusCode.Forbidden, low.StatusCode);
        using var read = await h.App.Client.GetAsync(ProductApiTests.Root + "/" + p.Id); Assert.Equal(HttpStatusCode.OK, read.StatusCode); var body = await read.Content.ReadAsStringAsync();
        foreach (var field in new[] { "avgCost", "unitCost", "onHand", "reserved", "storageKey", "createdBy", "updatedBy" }) Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
        var sales = await Staff(h, "sales"); await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        using var salesWrite = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, name = "Denied" }); Assert.Equal(HttpStatusCode.Forbidden, salesWrite.StatusCode);
        using var salesVariants = await h.App.Client.GetAsync(ProductApiTests.Root + "/" + p.Id + "/variants"); Assert.Equal(HttpStatusCode.Forbidden, salesVariants.StatusCode);
        Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
        using var anonymous = h.App.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using var denied = await anonymous.GetAsync(ProductApiTests.Root); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
    private static async Task<string> Staff(ProductHarness h, string role)
    {
        await using var scope = h.App.Factory.Services.CreateAsyncScope(); var email = role + Guid.NewGuid().ToString("N") + "@example.test";
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>().Handle(new(email, "Fixture", P009Harness.Password, [role], h.App.OwnerId, h.App.OwnerStamp), default); Assert.True(created.IsSuccess);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>().Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
        return email;
    }
}
