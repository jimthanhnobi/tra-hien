using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TeaPremium.IntegrationTests;

public sealed class OpenApiTests
{
    private static readonly string[] JsonTypes = ["array", "boolean", "null", "number", "object", "string"];

    [Fact]
    public async Task StoreCatalogPublishesExactlyNineReadOperations()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var store = paths.EnumerateObject().Where(p => p.Name.StartsWith("/api/v1/store/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(9, store.Length);
        Assert.All(store, path => Assert.Equal("get", Assert.Single(path.Value.EnumerateObject()).Name));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory/alerts", out var alertsPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory/opening-import", out _));
        var totalOperations = paths.EnumerateObject().Sum(p => p.Value.EnumerateObject()
            .Count(o => o.Name is "get" or "post" or "patch" or "put" or "delete"));
        Assert.Equal(95, totalOperations);
        var alertsGet = Assert.Single(alertsPath.EnumerateObject());
        Assert.Equal("get", alertsGet.Name);
        Assert.Contains(alertsGet.Value.GetProperty("parameters").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "limit" && x.GetProperty("in").GetString() == "query");

        var parameters = paths.GetProperty("/api/v1/store/products").GetProperty("get")
            .GetProperty("parameters").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        AssertOpenApiEnum(parameters["sort"].GetProperty("schema"), "newest", "price_asc", "price_desc");
        var attributes = parameters["attributes"];
        Assert.Equal("query", attributes.GetProperty("in").GetString());
        // form and explode=true are the OpenAPI defaults for a query parameter.
        if (attributes.TryGetProperty("style", out var style)) Assert.Equal("form", style.GetString());
        if (attributes.TryGetProperty("explode", out var explode)) Assert.True(explode.GetBoolean());
        var schema = attributes.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", schema.GetProperty("patternProperties").GetProperty(@"^attr\.[a-z0-9_]{2,40}$").GetProperty("type").GetString());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "StoreProductCard", "StoreVariant" })
            AssertOpenApiEnum(schemas.GetProperty(name).GetProperty("properties").GetProperty("availability"), "in_stock", "out_of_stock", "sold");
        foreach (var name in new[] { "InventoryAlertsView", "LowStockAlertItem", "InventoryMismatchItem" })
        {
            var props = schemas.GetProperty(name).GetProperty("properties");
            Assert.All(props.EnumerateObject(), prop =>
                Assert.DoesNotContain("cost", prop.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertOpenApiEnum(JsonElement schema, params string[] values)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(values, schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task NormalApiStartupRetainsCommandLineHostOptions()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--environment", "CI", "--urls", "http://127.0.0.1:0", "--ConnectionStrings:Default=" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__Default"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Information";
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var log = JsonDocument.Parse(line);
                if (log.RootElement.GetProperty("Message").GetString() is { } message
                    && message.StartsWith("Now listening on: ", StringComparison.Ordinal))
                { address = message["Now listening on: ".Length..]; break; }
            }
            Assert.NotNull(address);
            using var client = new HttpClient { BaseAddress = new(address) };
            using var response = await client.GetAsync("/openapi/v1.json", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // Command-line CI wins only for ordinary API startup.
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stderr;
        }
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("CI")]
    public async Task ContractIsGeneratedWithoutDatabase(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(environment).UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.1.", document.RootElement.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("paths").ValueKind);
    }

    [Fact]
    public async Task SettingsValuesAreExplicitRecursiveJsonForBothGetAndPut()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "SettingsGroup", "UpdateSettingsRequest" })
        {
            var values = schemas.GetProperty(name).GetProperty("properties").GetProperty("values");
            Assert.Equal("object", values.GetProperty("type").GetString());
            Assert.Equal("#/components/schemas/SettingsJsonValue", values.GetProperty("additionalProperties").GetProperty("$ref").GetString());
        }
        var types = schemas.GetProperty("SettingsJsonValue").GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(JsonTypes, types.Select(x => x.GetProperty("type").GetString()).Order().ToArray());
        Assert.Equal("#/components/schemas/SettingsJsonValue", types.Single(x => x.GetProperty("type").GetString() == "array").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/SettingsJsonValue", types.Single(x => x.GetProperty("type").GetString() == "object").GetProperty("additionalProperties").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task ProductionDoesNotPublishContract()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
