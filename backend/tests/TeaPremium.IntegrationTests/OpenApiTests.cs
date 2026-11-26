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
        Assert.Equal(115, totalOperations);
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/payments", out var recordPaymentPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/payments/{id}/void", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/payments/awaiting", out var awaitingPaymentsPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/refunds", out var createRefundPath));
        Assert.False(paths.TryGetProperty("/api/v1/admin/cod-settlements", out _));
        Assert.Contains(recordPaymentPath.GetProperty("post").GetProperty("parameters").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "Idempotency-Key"
                && x.GetProperty("in").GetString() == "header"
                && x.GetProperty("required").GetBoolean());
        Assert.Contains(createRefundPath.GetProperty("post").GetProperty("parameters").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "Idempotency-Key"
                && x.GetProperty("in").GetString() == "header"
                && x.GetProperty("required").GetBoolean());
        var awaitingParams = awaitingPaymentsPath.GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.True(awaitingParams["type"].GetProperty("required").GetBoolean());
        AssertOpenApiEnum(awaitingParams["type"].GetProperty("schema"), "bank_transfer", "cod");
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers", out var customersPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/import", out var customerImportPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/correct-identity", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/orders", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/tags", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/addresses", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/addresses/{aid}", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/tags", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/tags/{id}", out _));
        var customerListParams = customersPath.GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        AssertOpenApiEnum(customerListParams["group"].GetProperty("schema"), "retail", "wholesale", "reseller");
        AssertOpenApiEnum(customerListParams["contactStatus"].GetProperty("schema"), "active", "do_not_contact", "erased");
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

        foreach (var name in new[] { "CustomerSummaryView", "CustomerDetailView", "CustomerAccountSummaryView", "CustomerOrderSummaryView", "CustomerImportResult", "CustomerImportSummary", "CustomerImportRow" })
        {
            var props = schemas.GetProperty(name).GetProperty("properties");
            Assert.All(props.EnumerateObject(), prop =>
            {
                Assert.DoesNotContain("cost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("profit", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("securityStamp", prop.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        AssertOpenApiEnum(schemas.GetProperty("CustomerDetailView").GetProperty("properties").GetProperty("customerGroup"), "retail", "wholesale", "reseller");
        AssertOpenApiEnum(schemas.GetProperty("CustomerDetailView").GetProperty("properties").GetProperty("origin"), "legacy_import", "web", "admin");
        AssertOpenApiEnum(schemas.GetProperty("CustomerDetailView").GetProperty("properties").GetProperty("contactStatus"), "active", "do_not_contact", "erased");

        var importPost = customerImportPath.GetProperty("post");
        var importDryRun = importPost.GetProperty("parameters").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "dryRun");
        Assert.Equal("query", importDryRun.GetProperty("in").GetString());
        Assert.True(importDryRun.GetProperty("required").GetBoolean());
        Assert.Equal("boolean", importDryRun.GetProperty("schema").GetProperty("type").GetString());
        var importFile = importPost.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data")
            .GetProperty("schema").GetProperty("properties").GetProperty("file");
        Assert.Equal("string", importFile.GetProperty("type").GetString());
        Assert.Equal("binary", importFile.GetProperty("format").GetString());
        AssertOpenApiEnum(schemas.GetProperty("CustomerImportRow").GetProperty("properties").GetProperty("status"),
            "imported", "would_import", "skipped", "error");
        AssertOpenApiEnum(schemas.GetProperty("CustomerImportRow").GetProperty("properties").GetProperty("reasonCode"),
            "imported", "would_import", "duplicate_in_file", "existing_phone_skipped",
            "missing_full_name", "invalid_full_name", "missing_contact", "invalid_phone",
            "invalid_zalo_name", "invalid_customer_group", "invalid_tag", "invalid_note", "invalid_had_purchase");
        var importWarnings = schemas.GetProperty("CustomerImportRow").GetProperty("properties").GetProperty("warnings");
        Assert.Equal("array", importWarnings.GetProperty("type").GetString());
        AssertOpenApiEnum(importWarnings.GetProperty("items"),
            "zalo_only_reimport_risk", "possible_duplicate_zalo_customer");

        var importSummarySchema = schemas.GetProperty("CustomerImportSummary");
        var importSummaryProps = importSummarySchema.GetProperty("properties");
        var importSummaryRequired = importSummarySchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("committed", importSummaryRequired);
        Assert.Contains("newTags", importSummaryRequired);
        Assert.Equal("boolean", importSummaryProps.GetProperty("committed").GetProperty("type").GetString());
        var importNewTags = importSummaryProps.GetProperty("newTags");
        Assert.Equal("array", importNewTags.GetProperty("type").GetString());
        Assert.Equal("string", importNewTags.GetProperty("items").GetProperty("type").GetString());

        foreach (var name in new[] { "PaymentRecordedView", "PaymentVoidedView", "RefundRecordedView", "AwaitingPaymentsPage", "AwaitingBankTransferItem", "AwaitingCodPaymentItem" })
        {
            var props = schemas.GetProperty(name).GetProperty("properties");
            Assert.All(props.EnumerateObject(), prop =>
            {
                Assert.DoesNotContain("cost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("profit", prop.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        AssertOpenApiEnum(schemas.GetProperty("RecordOrderPaymentRequest").GetProperty("properties").GetProperty("method"), "bank_transfer", "cash");
        AssertOpenApiEnum(schemas.GetProperty("CreateOrderRefundRequest").GetProperty("properties").GetProperty("kind"), "overpayment", "return", "cancellation", "adjustment");
        AssertOpenApiEnum(schemas.GetProperty("CreateOrderRefundRequest").GetProperty("properties").GetProperty("method"), "bank_transfer", "cash");
        Assert.Equal("integer", schemas.GetProperty("CreateOrderRefundRequest").GetProperty("properties").GetProperty("shippingAmountVnd").GetProperty("type").GetString());
        AssertOpenApiEnum(schemas.GetProperty("RefundRecordedView").GetProperty("properties").GetProperty("kind"), "overpayment", "return", "cancellation", "adjustment");
        Assert.Equal("integer", schemas.GetProperty("RefundRecordedView").GetProperty("properties").GetProperty("shippingAmountVnd").GetProperty("type").GetString());
        Assert.Equal("integer", schemas.GetProperty("AwaitingBankTransferItem").GetProperty("properties").GetProperty("effectivePaidVnd").GetProperty("type").GetString());
        AssertOpenApiEnum(schemas.GetProperty("PaymentVoidedView").GetProperty("properties").GetProperty("status"), "voided");
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
