using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-05 focused G4 F1/F2/F3; BR-CAT-04.6/15, AC-P1-02, 05 §1/4.2.
public sealed class ProductG4FindingTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task ActiveProductRejectsDuplicateAxisPatchWithoutAnyPersistedEffect()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await ProductPublicationTests.Ready(h, await h.Create(), 150);
        p = await h.Change(p, "/variants", new { priceVnd = 2000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) } });
        p = await h.Change(p, "/publish", new { }); var v = p.Variants[1]; var before = await Fingerprint(h);
        using var result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id,
            new { v.RowVersion, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } });
        await Problem(result, HttpStatusCode.UnprocessableEntity, "variant_requirements_not_met");
        Assert.Contains("variant_axis_duplicate", await result.Content.ReadAsStringAsync()); Assert.Equal(before, await Fingerprint(h));
    }
    [Fact]
    public async Task MalformedCommandControlsAreNotSilentlyIgnored()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var before = await Fingerprint(h);
        using var result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id,
            new { p.RowVersion, subtitle = "Must rollback", noindexReason = new { malformed = true }, confirmTypeChange = "wrong" });
        await Problem(result, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
    }
    [Theory]
    [InlineData("noindexReason", "{}")]
    [InlineData("noindexReason", "[]")]
    [InlineData("noindexReason", "42")]
    [InlineData("noindexReason", "true")]
    [InlineData("noindexReason", "false")]
    [InlineData("confirmTypeChange", "\"wrong\"")]
    [InlineData("confirmTypeChange", "{}")]
    [InlineData("confirmTypeChange", "[]")]
    [InlineData("confirmTypeChange", "1")]
    [InlineData("rowVersion", "{}")]
    [InlineData("rowVersion", "false")]
    public async Task InvalidControlTypesReturn400AndPreserveValuesVersionsAuditAndAllOutbox(string field, string raw)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var before = await Fingerprint(h);
        var body = new Dictionary<string, JsonElement> { ["rowVersion"] = JsonSerializer.SerializeToElement(p.RowVersion), ["subtitle"] = JsonSerializer.SerializeToElement("Must not save") };
        body[field] = JsonSerializer.Deserialize<JsonElement>(raw);
        using var result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, body);
        await Problem(result, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
    }
    [Theory]
    [InlineData("{\"rowVersion\":{}}")]
    [InlineData("{\"rowVersion\":[]}")]
    [InlineData("{\"rowVersion\":true}")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task MalformedNestedCreationControlDoesNotAllocateOrPersist(string raw)
    {
        await using var h = await ProductHarness.Create(fixture); var before = await Fingerprint(h);
        using var result = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root,
            new { name = "No mutation", productTypeId = h.Type, defaultVariant = JsonSerializer.Deserialize<JsonElement>(raw) });
        await Problem(result, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
    }
    [Fact]
    public async Task OptionalOmittedNullAndValidControlsKeepTheirPublishedSemantics()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        p = await h.Patch(p, new { subtitle = "Controls omitted" }); Assert.Equal("Controls omitted", p.Subtitle);
        p = await h.Patch(p, new { subtitle = "Controls null", noindexReason = (string?)null, confirmTypeChange = (bool?)null }); Assert.Equal("Controls null", p.Subtitle);
        p = await h.Patch(p, new { noindexReason = "Optional reason", confirmTypeChange = false });
        p = await h.Patch(p, new { robotsNoindex = true, noindexReason = "Manual noindex reason", confirmTypeChange = true }); Assert.True(p.RobotsNoindex);
        var before = await Fingerprint(h);
        using var unknown = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id, new { p.RowVersion, unknownControl = true });
        await Problem(unknown, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
        var next = await h.Create(initial: new { name = "Valid optional nested version", productTypeId = h.Type, defaultVariant = new { rowVersion = (string?)null } }); Assert.Single(next.Variants);
    }
    [Theory]
    [InlineData("create-name")]
    [InlineData("create-type")]
    [InlineData("patch-version")]
    [InlineData("variant-version")]
    public async Task RequiredNonnullableHttpFieldsRejectExplicitNull(string input)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var before = await Fingerprint(h);
        var body = new Dictionary<string, object?> { ["rowVersion"] = p.RowVersion };
        var path = ProductApiTests.Root + "/" + p.Id; var method = HttpMethod.Patch;
        switch (input)
        {
            case "create-name": body = new() { ["productTypeId"] = h.Type, ["name"] = null }; path = ProductApiTests.Root; method = HttpMethod.Post; break;
            case "create-type": body = new() { ["productTypeId"] = null, ["name"] = "Null type" }; path = ProductApiTests.Root; method = HttpMethod.Post; break;
            case "patch-version": body["rowVersion"] = null; break;
            case "variant-version": body["rowVersion"] = null; path += "/variants/" + p.Variants[0].Id; break;
        }
        using var response = await h.App.MutateAsync(method, path, body); await Problem(response, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
    }
    [Theory]
    [InlineData("create")]
    [InlineData("activate")]
    [InlineData("clear-axis")]
    public async Task ActiveAggregateRejectsEveryAxisBreakingMutationAndPreservesInactiveBehavior(string mutation)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await ProductPublicationTests.Ready(h, await h.Create(), 150);
        p = await h.Change(p, "/variants", new { status = "inactive", priceVnd = 2000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } });
        p = await h.Change(p, "/publish", new { }); var v = p.Variants[1];
        if (mutation == "clear-axis")
        {
            p = await h.Change(p, "/variants/" + v.Id, new { status = "active", attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) } }, HttpMethod.Patch, v.RowVersion); v = p.Variants[1];
        }
        var before = await Fingerprint(h); HttpResponseMessage result;
        if (mutation == "create") result = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/variants", new { p.RowVersion, priceVnd = 2000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } });
        else if (mutation == "activate") result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, status = "active" });
        else result = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, attributes = Array.Empty<ProductValue>() });
        using (result) { await Problem(result, HttpStatusCode.UnprocessableEntity, "variant_requirements_not_met"); Assert.Contains(mutation == "clear-axis" ? "variant_axis_missing" : "variant_axis_duplicate", await result.Content.ReadAsStringAsync()); }
        Assert.Equal(before, await Fingerprint(h));
        p = await h.Patch(p, new { subtitle = "Unrelated valid change" }); Assert.Equal("Unrelated valid change", p.Subtitle);
        if (mutation != "clear-axis")
        {
            v = p.Variants[1]; p = await h.Change(p, "/variants/" + v.Id, new { name = "Inactive remains excluded", priceVnd = (long?)null }, HttpMethod.Patch, v.RowVersion);
            Assert.Equal("inactive", p.Variants[1].Status); Assert.Null(p.Variants[1].PriceVnd);
        }
    }
    [Fact]
    public async Task AxisUniquenessUsesTheFullCombinationAndMultipleActiveCondition()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await ProductPublicationTests.Ready(h, await h.Create(), 150);
        var secondAxis = Guid.CreateVersion7(); await h.App.ExecuteAsync($"INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type,scope,is_variant_axis) VALUES ('{secondAxis}','{h.Type}','finish','Finish','text','variant',true)");
        var v = p.Variants[0]; p = await h.Change(p, "/variants/" + v.Id, new { attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180), new ProductValue(secondAxis, ValueText: "A") } }, HttpMethod.Patch, v.RowVersion);
        p = await h.Change(p, "/variants", new { priceVnd = 1000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180), new ProductValue(secondAxis, ValueText: "B") } });
        p = await h.Change(p, "/publish", new { }); Assert.Equal(2, p.Variants.Count);
        // One active variant is not required to carry an optional variant axis (BR-CAT-04.6).
        v = p.Variants[1]; p = await h.Change(p, "/variants/" + v.Id, new { status = "inactive" }, HttpMethod.Patch, v.RowVersion);
        v = p.Variants[0]; p = await h.Change(p, "/variants/" + v.Id, new { attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } }, HttpMethod.Patch, v.RowVersion);
        Assert.Equal("active", p.Status);
    }
    [Theory]
    [InlineData("ProductCreate", "name")]
    [InlineData("ProductCreate", "productTypeId")]
    [InlineData("ProductPatch", "rowVersion")]
    [InlineData("VariantPatch", "rowVersion")]
    public async Task RequiredOpenApiFieldsAreRequiredAndNonnullable(string schema, string property)
    {
        using var document = await OpenApi(); var input = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schema);
        Assert.Contains(input.GetProperty("required").EnumerateArray(), x => x.GetString() == property);
        Assert.False(AllowsNull(input.GetProperty("properties").GetProperty(property)));
    }
    [Fact]
    public async Task OptionalOpenApiPropertiesKeepOmissionIndependentFromExplicitNull()
    {
        using var document = await OpenApi(); var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (name, nonnullable, nullable) in new (string, string[], string[])[]
        {
            ("ProductPatch", ["name", "productTypeId", "slug", "isFeatured", "robotsNoindex", "seoKeepAfterSold", "attributes", "categoryIds", "relatedProductIds"], ["subtitle", "descriptionHtml", "materialId", "noindexReason", "confirmTypeChange"]),
            ("VariantPatch", ["sku", "position", "status", "isUniquePiece", "lowStockThreshold", "attributes"], ["name", "priceVnd", "weightG", "barcode", "packageDimsCm"]),
            ("ProductMediaCreate", ["rowVersion", "assetId", "altText", "sortOrder"], ["variantId"])
        })
        {
            var schema = schemas.GetProperty(name); var properties = schema.GetProperty("properties");
            foreach (var field in nonnullable) Assert.False(AllowsNull(properties.GetProperty(field)), name + "." + field);
            foreach (var field in nullable) Assert.True(AllowsNull(properties.GetProperty(field)), name + "." + field);
        }
        Assert.Equal("rowVersion", string.Join(',', schemas.GetProperty("ProductPatch").GetProperty("required").EnumerateArray().Select(x => x.GetString())));
        Assert.Equal("rowVersion", string.Join(',', schemas.GetProperty("VariantPatch").GetProperty("required").EnumerateArray().Select(x => x.GetString())));
        Assert.Equal("name,productTypeId", string.Join(',', schemas.GetProperty("ProductCreate").GetProperty("required").EnumerateArray().Select(x => x.GetString())));
        Assert.False(AllowsNull(schemas.GetProperty("ProductCreate").GetProperty("properties").GetProperty("defaultVariant")));
    }
    [Theory]
    [InlineData("product", "name")]
    [InlineData("product", "productTypeId")]
    [InlineData("product", "slug")]
    [InlineData("product", "isFeatured")]
    [InlineData("product", "robotsNoindex")]
    [InlineData("product", "attributes")]
    [InlineData("variant", "sku")]
    [InlineData("variant", "position")]
    [InlineData("variant", "status")]
    [InlineData("variant", "isUniquePiece")]
    [InlineData("variant", "attributes")]
    [InlineData("media", "assetId")]
    [InlineData("media", "altText")]
    [InlineData("media", "sortOrder")]
    public async Task OptionalNonnullableFieldsAreAllowedOmittedButRejectNull(string target, string field)
    {
        await using var h = await ProductHarness.Create(fixture); var p = await ProductPublicationTests.Ready(h, await h.Create(), 150);
        var path = ProductApiTests.Root + "/" + p.Id; var version = p.RowVersion;
        if (target == "variant") { path += "/variants/" + p.Variants[0].Id; version = p.Variants[0].RowVersion; }
        if (target == "media") path += "/media/" + p.Media[0].Id;
        var before = await Fingerprint(h); using var response = await h.App.MutateAsync(HttpMethod.Patch, path,
            new Dictionary<string, object?> { ["rowVersion"] = version, [field] = null });
        await Problem(response, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(before, await Fingerprint(h));
    }
    internal static bool AllowsNull(JsonElement property) => property.TryGetProperty("type", out var type)
        && (type.ValueKind == JsonValueKind.String ? type.GetString() == "null" : type.EnumerateArray().Any(x => x.GetString() == "null"))
        || property.TryGetProperty("oneOf", out var one) && one.EnumerateArray().Any(AllowsNull)
        || property.TryGetProperty("anyOf", out var any) && any.EnumerateArray().Any(AllowsNull);
    internal static async Task<JsonDocument> OpenApi()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient(); return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }
    internal static async Task Problem(HttpResponseMessage result, HttpStatusCode status, string code)
    { Assert.True(result.StatusCode == status, await result.Content.ReadAsStringAsync()); Assert.Equal("application/problem+json", result.Content.Headers.ContentType?.MediaType); using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync()); Assert.Equal(code, body.RootElement.GetProperty("code").GetString()); }
    internal static async Task<string> Fingerprint(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        return JsonSerializer.Serialize(new
        {
            products = await db.Set<Product>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            variants = await db.Set<Variant>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            productValues = await db.Set<ProductAttributeValue>().AsNoTracking().OrderBy(x => x.ProductId).ThenBy(x => x.AttributeId).ToArrayAsync(),
            variantValues = await db.Set<VariantAttributeValue>().AsNoTracking().OrderBy(x => x.VariantId).ThenBy(x => x.AttributeId).ToArrayAsync(),
            audit = await db.AuditLogs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            outbox = await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
