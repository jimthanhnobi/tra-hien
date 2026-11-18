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
