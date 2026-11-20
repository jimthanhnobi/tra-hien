using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class ProductApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    internal const string Root = "/api/v1/admin/products";
    [Theory]
    [InlineData("1")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task MalformedArchiveRedirectIsAValidationProblemWithoutPartialStateChange(string value)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        using var response = await h.App.MutateAsync(HttpMethod.Post, Root + "/" + p.Id + "/archive", new { p.RowVersion, redirectTo = JsonSerializer.Deserialize<JsonElement>(value) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("validation_failed", await response.Content.ReadAsStringAsync()); Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
    }
    [Fact]
    public async Task SliceADraftCrudRelationsTypedValuesAndTypeChangePreserveIdentityAndSku()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await h.Create(); Assert.Equal("draft", p.Status); var variant = Assert.Single(p.Variants); Assert.Null(variant.PriceVnd); Assert.Equal(p.SkuBase, variant.Sku);
        using var get = await h.App.Client.GetAsync(Root + "/" + p.Id); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        p = await h.Patch(p, new { subtitle = "Ấm tử sa", attributes = new[] { new ProductValue(h.ProductAttribute, ValueNumber: 0) }, categoryIds = new[] { h.Category }, primaryCategoryId = h.Category });
        Assert.Equal(0, Assert.Single(p.Attributes).ValueNumber);
        using var list = await h.App.Client.GetAsync(Root + "?q=am%20tu%20sa&type=" + h.Type); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await list.Content.ReadFromJsonAsync<CatalogPage<ProductData>>())!.Items);
        using var stale = await h.App.MutateAsync(HttpMethod.Patch, Root + "/" + p.Id, new { rowVersion = "AQAAAA==", name = "Stale" }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var warning = await h.App.MutateAsync(HttpMethod.Patch, Root + "/" + p.Id, new { p.RowVersion, productTypeId = h.OtherType }); Assert.Equal(HttpStatusCode.Conflict, warning.StatusCode);
        var sku = p.SkuBase;
        p = await h.Patch(p, new { productTypeId = h.OtherType, confirmTypeChange = true }); Assert.Empty(p.Attributes); Assert.Equal(sku, p.SkuBase); Assert.Equal(sku, p.Variants[0].Sku);
        using var deletion = await h.App.MutateAsync(HttpMethod.Delete, Root + "/" + p.Id, new { p.RowVersion }); Assert.Equal(HttpStatusCode.MethodNotAllowed, deletion.StatusCode);
    }
    [Theory]
    [InlineData("{\"status\":\"active\"}")]
    [InlineData("{\"skuBase\":\"AM-XX-9999\"}")]
    [InlineData("{\"descriptionWordCount\":150}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"categoryIds\":null}")]
    [InlineData("{\"attributes\":[null]}")]
    [InlineData("{\"name\":\"bad\\u0000value\"}")]
    [InlineData("{\"name\":\"one\",\"name\":\"two\"}")]
    public async Task SliceARejectsUntrustedOrServerOwnedFieldsWithoutPartialWrite(string json)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        // Keep raw duplicate keys for the duplicate-input case.
        var raw = "{\"rowVersion\":\"" + p.RowVersion + "\"," + json[1..];
        using var csrf = await h.App.Client.GetAsync("/api/v1/admin/auth/csrf"); var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Patch, Root + "/" + p.Id) { Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json") }; request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await h.App.Client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
    }
    [Fact]
    public async Task SliceATypedValidatorRejectsCrossTypeScopeRepresentationAndPreservesFalse()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        foreach (var value in new[] { new ProductValue(h.ProductAttribute, ValueText: "wrong"), new ProductValue(h.ProductAttribute, ValueNumber: 1.001m),
            new ProductValue(h.VariantAttribute, ValueNumber: 1), new ProductValue(h.ProductAttribute, ValueNumber: 1, ValueBool: false), new ProductValue(Guid.CreateVersion7(), ValueBool: false) })
        {
            using var bad = await h.App.MutateAsync(HttpMethod.Patch, Root + "/" + p.Id, new { p.RowVersion, attributes = new[] { value } }); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
        }
        p = await h.Patch(p, new { attributes = new[] { new ProductValue(h.BooleanAttribute, ValueBool: false) } }); Assert.False(Assert.Single(p.Attributes).ValueBool);
    }
}

internal sealed class ProductHarness : IAsyncDisposable
{
    public required P009Harness App { get; init; }
    public Guid Type { get; } = Guid.CreateVersion7(); public Guid OtherType { get; } = Guid.CreateVersion7();
    public Guid Category { get; } = Guid.CreateVersion7(); public Guid Material { get; } = Guid.CreateVersion7();
    public Guid ProductAttribute { get; } = Guid.CreateVersion7(); public Guid VariantAttribute { get; } = Guid.CreateVersion7(); public Guid BooleanAttribute { get; } = Guid.CreateVersion7();
    public static async Task<ProductHarness> Create(PlatformDatabaseFixture fixture, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var h = new ProductHarness { App = await P009Harness.CreateAsync(fixture, services) }; await h.App.LoginAsync();
        await h.App.ExecuteAsync($"""
INSERT INTO catalog.product_types(id,code,name,sku_prefix) VALUES ('{h.Type}','p105_teapot','Ấm tử sa','AM'),('{h.OtherType}','p105_cup','Chén','CH');
INSERT INTO catalog.materials(id,slug,name,kind,sku_code) VALUES ('{h.Material}','p105-clay','Tử nê','clay','TN');
INSERT INTO catalog.categories(id,slug,name,status,first_published_at) VALUES ('{h.Category}','p105-tea','Trà cụ','visible',now());
INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type,scope,is_required_for_publish) VALUES
 ('{h.ProductAttribute}','{h.Type}','year','Năm','number','product',false),
 ('{h.BooleanAttribute}','{h.Type}','handmade','Thủ công','boolean','product',false),
 ('{h.VariantAttribute}','{h.Type}','capacity_ml','Dung tích','number','variant',true);
UPDATE catalog.attribute_definitions SET is_variant_axis=true WHERE id='{h.VariantAttribute}';
"""); return h;
    }
    public async Task<ProductData> Create(string name = "Ấm trà", object? initial = null)
    {
        using var response = await App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, initial ?? new { name, productTypeId = Type, materialId = Material });
        return await Read(response, HttpStatusCode.Created);
    }
    public async Task<ProductData> Get(Guid id) => (await App.Client.GetFromJsonAsync<ProductData>(ProductApiTests.Root + "/" + id))!;
    public async Task<ProductData> Patch(ProductData p, object fields) => await Change(p, "", fields, HttpMethod.Patch);
    public async Task<ProductData> Change(ProductData p, string suffix, object fields, HttpMethod? method = null, string? version = null)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        body["rowVersion"] = JsonSerializer.SerializeToElement(version ?? p.RowVersion);
        using var response = await App.MutateAsync(method ?? HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + suffix, body);
        return await Read(response, HttpStatusCode.OK);
    }
    public static async Task<ProductData> Read(HttpResponseMessage response, HttpStatusCode expected)
    { Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<ProductData>())!; }
    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}
