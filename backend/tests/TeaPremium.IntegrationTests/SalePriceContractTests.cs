using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TeaPremium.IntegrationTests;

public sealed class SalePriceContractTests
{
    private static readonly string[] CreateFields = ["endsAt", "rowVersion", "salePriceVnd", "startsAt"];

    [Fact]
    public async Task OpenApiDescribesAllThreePricingOperationsAndPreservesMultipartImport()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var root = SalePriceApiTests.Route + "/{id}/sale-prices";
        var collection = paths.GetProperty(root);
        Assert.Equal(2, collection.EnumerateObject().Count());
        collection.GetProperty("get").GetProperty("responses").GetProperty("200");
        var post = collection.GetProperty("post");
        AssertRequest(post, "CreateSalePriceRequest", "201");
        var remove = Assert.Single(paths.GetProperty(root + "/{salePriceId}").EnumerateObject());
        Assert.Equal("delete", remove.Name);
        AssertRequest(remove.Value, "DeleteSalePriceRequest", "204");
        Assert.Contains(remove.Value.GetProperty("parameters").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "salePriceId");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var create = schemas.GetProperty("CreateSalePriceRequest");
        Assert.Equal(CreateFields, create.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal));
        Assert.False(create.GetProperty("additionalProperties").GetBoolean());
        var properties = create.GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("salePriceVnd").GetProperty("type").GetString());
        Assert.Equal("int64", properties.GetProperty("salePriceVnd").GetProperty("format").GetString());
        Assert.Equal("date-time", properties.GetProperty("startsAt").GetProperty("format").GetString());
        Assert.Equal("date-time", properties.GetProperty("endsAt").GetProperty("format").GetString());
        Assert.Equal("rowVersion", Assert.Single(schemas.GetProperty("DeleteSalePriceRequest").GetProperty("required").EnumerateArray()).GetString());
        var import = paths.GetProperty(ProductApiTests.Root + "/import").GetProperty("post");
        Assert.True(import.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
    }

    private static void AssertRequest(JsonElement operation, string schema, string success)
    {
        var body = operation.GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal("#/components/schemas/" + schema, body.GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString());
        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty(success, out _));
        Assert.True(responses.TryGetProperty("409", out _));
        Assert.Equal("#/components/schemas/ProblemDetails", responses.GetProperty("415")
            .GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
    }
}
