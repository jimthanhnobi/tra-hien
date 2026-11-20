using System.Net;
using System.Net.Http.Json;
using TeaPremium.Application.Catalog;

namespace TeaPremium.IntegrationTests;

public sealed class ProductValueTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task OptionAssignmentsUseRealDefinitionsAndInactiveHistoricalValuesRemainReadable()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create();
        var root = $"/api/v1/admin/product-types/{h.Type}/attributes";
        using var created = await h.App.MutateAsync(HttpMethod.Post, root, new { code = "finish", name = "Bề mặt", dataType = "option", scope = "variant" }); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var definition = (await created.Content.ReadFromJsonAsync<AttributeView>())!;
        using var optionResponse = await h.App.MutateAsync(HttpMethod.Post, root + "/" + definition.Id + "/options", new { code = "matte", label = "Mờ" }); Assert.Equal(HttpStatusCode.Created, optionResponse.StatusCode);
        var option = (await optionResponse.Content.ReadFromJsonAsync<OptionView>())!; var v = p.Variants[0];
        p = await h.Change(p, "/variants/" + v.Id, new { attributes = new[] { new ProductValue(definition.Id, OptionId: option.Id) } }, HttpMethod.Patch, v.RowVersion);
        using var deactivate = await h.App.MutateAsync(HttpMethod.Patch, root + "/" + definition.Id + "/options/" + option.Id, new { option.RowVersion, isActive = false }); Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        p = await h.Patch(p, new { subtitle = "Keep historical inactive values" }); Assert.Equal(option.Id, p.Variants[0].Attributes[0].OptionId);
        using var newAssignment = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/variants", new { p.RowVersion, attributes = new[] { new ProductValue(definition.Id, OptionId: option.Id) } }); Assert.Equal(HttpStatusCode.BadRequest, newAssignment.StatusCode);
        var currentDefinition = (await h.App.Client.GetFromJsonAsync<AttributeView>(root + "/" + definition.Id))!;
        using var disable = await h.App.MutateAsync(HttpMethod.Patch, root + "/" + definition.Id, new { currentDefinition.RowVersion, isActive = false }); Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        p = await h.Patch(p, new { subtitle = "Keep historical inactive definition" }); Assert.Equal(option.Id, p.Variants[0].Attributes[0].OptionId);
    }
    [Fact]
    public async Task VariantNumberBooleanAndDimensionsWritesUseTypedValidatorAndRejectWrongScope()
    {
        await using var h = await ProductHarness.Create(fixture); var p = await h.Create(); var v = p.Variants[0];
        using var wrongScope = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, attributes = new[] { new ProductValue(h.ProductAttribute, ValueNumber: 10) } }); Assert.Equal(HttpStatusCode.BadRequest, wrongScope.StatusCode);
        using var precision = await h.App.MutateAsync(HttpMethod.Patch, ProductApiTests.Root + "/" + p.Id + "/variants/" + v.Id, new { v.RowVersion, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180.001m) } }); Assert.Equal(HttpStatusCode.BadRequest, precision.StatusCode);
        p = await h.Change(p, "/variants/" + v.Id, new { packageDimsCm = new { l = 20, w = 15, h = 12 }, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180.00m) } }, HttpMethod.Patch, v.RowVersion);
        Assert.Contains("20", p.Variants[0].PackageDimsCm); Assert.Equal(180, p.Variants[0].Attributes[0].ValueNumber);
    }
}
