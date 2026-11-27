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
    public async Task StoreCatalogPublishesExactlyElevenReadOperations()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var store = paths.EnumerateObject().Where(p => p.Name.StartsWith("/api/v1/store/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, store.Length);
        Assert.All(store, path => Assert.Equal("get", Assert.Single(path.Value.EnumerateObject()).Name));
        Assert.True(paths.TryGetProperty("/api/v1/store/provinces", out var storeProvincesPath));
        Assert.True(storeProvincesPath.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/v1/store/provinces/{code}/wards", out var storeWardsPath));
        Assert.True(storeWardsPath.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory/alerts", out var alertsPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/inventory/opening-import", out _));
        var totalOperations = paths.EnumerateObject().Sum(p => p.Value.EnumerateObject()
            .Count(o => o.Name is "get" or "post" or "patch" or "put" or "delete"));
        Assert.Equal(133, totalOperations);
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders", out var adminOrdersPath));
        Assert.True(adminOrdersPath.TryGetProperty("get", out var listOrdersOp));
        Assert.True(adminOrdersPath.TryGetProperty("post", out var createOrderOp));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}", out var adminOrderItemPath));
        Assert.True(adminOrderItemPath.TryGetProperty("get", out _));
        Assert.True(adminOrderItemPath.TryGetProperty("patch", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/confirm", out var confirmOrderPath));
        Assert.True(confirmOrderPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/cancel", out var cancelOrderPath));
        Assert.True(cancelOrderPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/reorder", out var reorderOrderPath));
        Assert.True(reorderOrderPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/packing-slip", out var packingSlipPath));
        Assert.True(packingSlipPath.TryGetProperty("get", out var packingSlipOp));
        Assert.True(packingSlipOp.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/html", out _));
        // O6a = A: extend-payment stays in P3-03 (07-roadmap.md:125), MUST NOT exist in P2-06.
        Assert.False(paths.TryGetProperty("/api/v1/admin/orders/{id}/extend-payment", out _));
        Assert.Contains(createOrderOp.GetProperty("parameters").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "Idempotency-Key"
                && x.GetProperty("in").GetString() == "header"
                && x.GetProperty("required").GetBoolean());
        var listOrderParams = listOrdersOp.GetProperty("parameters").EnumerateArray()
            .ToDictionary(x => x.GetProperty("name").GetString()!);
        AssertOpenApiEnum(listOrderParams["flag"].GetProperty("schema"), "needs_refund", "overpaid", "expiring");
        AssertOpenApiEnum(listOrderParams["sort"].GetProperty("schema"),
            "placedAt:desc", "placedAt:asc", "totalVnd:desc", "totalVnd:asc", "deliveredAt:desc", "deliveredAt:asc");
        AssertOpenApiEnum(listOrderParams["origin"].GetProperty("schema"), "web", "admin");
        Assert.True(paths.TryGetProperty("/api/v1/admin/orders/{id}/shipments", out var createShipmentPath));
        Assert.True(createShipmentPath.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/shipments/{id}", out var patchShipmentPath));
        Assert.True(patchShipmentPath.TryGetProperty("patch", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/shipments/{id}/events", out var shipmentEventsPath));
        Assert.True(shipmentEventsPath.TryGetProperty("post", out _));
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
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/interactions", out var interactionsPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/interactions/{iid}", out var interactionItemPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/customers/{id}/interactions/{iid}/complete-follow-up", out var completeFollowUpPath));
        Assert.True(paths.TryGetProperty("/api/v1/admin/follow-ups", out var followUpsPath));
        Assert.False(paths.TryGetProperty("/api/v1/admin/customers/{id}/campaigns", out _));
        Assert.True(interactionsPath.TryGetProperty("get", out _));
        Assert.True(interactionsPath.TryGetProperty("post", out _));
        Assert.True(interactionItemPath.TryGetProperty("patch", out _));
        Assert.True(completeFollowUpPath.TryGetProperty("post", out _));
        var followUpParams = followUpsPath.GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        AssertOpenApiEnum(followUpParams["due"].GetProperty("schema"), "today", "overdue", "week");
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

        foreach (var name in new[] { "CustomerSummaryView", "CustomerDetailView", "CustomerAccountSummaryView", "CustomerOrderSummaryView", "CustomerImportResult", "CustomerImportSummary", "CustomerImportRow", "CustomerInteractionView", "FollowUpItemView" })
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
        var createInteractionSchema = schemas.GetProperty("CreateCustomerInteractionRequest");
        Assert.False(createInteractionSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(createInteractionSchema.GetProperty("properties").TryGetProperty("campaignId", out _));
        AssertOpenApiEnum(createInteractionSchema.GetProperty("properties").GetProperty("channel"),
            "zalo_personal", "zalo_oa", "phone", "facebook", "in_person", "email", "web", "other");
        AssertOpenApiEnum(createInteractionSchema.GetProperty("properties").GetProperty("kind"),
            "inquiry", "consultation", "quote", "follow_up", "complaint", "note");
        var patchInteractionSchema = schemas.GetProperty("PatchCustomerInteractionRequest");
        Assert.False(patchInteractionSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(patchInteractionSchema.GetProperty("properties").TryGetProperty("campaignId", out _));
        Assert.False(patchInteractionSchema.GetProperty("properties").TryGetProperty("followUpDoneAt", out _));
        Assert.False(patchInteractionSchema.GetProperty("properties").TryGetProperty("followUpResult", out _));
        AssertOpenApiEnum(patchInteractionSchema.GetProperty("properties").GetProperty("kind"),
            "inquiry", "consultation", "quote", "follow_up", "complaint", "note");
        AssertOpenApiEnum(schemas.GetProperty("CustomerInteractionView").GetProperty("properties").GetProperty("kind"),
            "inquiry", "consultation", "quote", "follow_up", "complaint", "note", "campaign_message");
        AssertOpenApiEnum(schemas.GetProperty("FollowUpItemView").GetProperty("properties").GetProperty("kind"),
            "inquiry", "consultation", "quote", "follow_up", "complaint", "note", "campaign_message");
        AssertOpenApiEnum(schemas.GetProperty("FollowUpItemView").GetProperty("properties").GetProperty("customerContactStatus"),
            "active", "do_not_contact");
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

        foreach (var name in new[] { "CreateOrderShipmentRequest", "PatchShipmentRequest", "RecordShipmentEventRequest" })
            Assert.False(schemas.GetProperty(name).GetProperty("additionalProperties").GetBoolean());
        AssertOpenApiEnum(
            schemas.GetProperty("RecordShipmentEventRequest").GetProperty("properties").GetProperty("status"),
            "picked_up", "in_transit", "delivered", "failed", "returned");
        AssertOpenApiEnum(
            schemas.GetProperty("ShipmentDetailView").GetProperty("properties").GetProperty("purpose"),
            "order", "replacement");
        AssertOpenApiEnum(
            schemas.GetProperty("ShipmentDetailView").GetProperty("properties").GetProperty("provider"),
            "manual", "ghn", "ghtk");
        AssertOpenApiEnum(
            schemas.GetProperty("ShipmentDetailView").GetProperty("properties").GetProperty("status"),
            "created", "picked_up", "in_transit", "delivered", "failed", "returned", "cancelled");
        AssertOpenApiEnum(
            schemas.GetProperty("ShipmentEventView").GetProperty("properties").GetProperty("source"),
            "manual", "webhook");
        foreach (var name in new[] { "ShipmentDetailView", "ShipmentEventView" })
        {
            var props = schemas.GetProperty(name).GetProperty("properties");
            Assert.All(props.EnumerateObject(), prop =>
            {
                Assert.DoesNotContain("unitCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("avgCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("profit", prop.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        // P2-06 Admin Orders & GAP-P214-01 Store Administrative schemas
        foreach (var name in new[] { "CreateAdminOrderRequest", "PatchAdminOrderRequest", "ConfirmAdminOrderRequest", "CancelAdminOrderRequest", "ReorderAdminOrderRequest" })
            Assert.False(schemas.GetProperty(name).GetProperty("additionalProperties").GetBoolean());

        // POST /api/v1/admin/orders has 201 (AdminOrderCreatedView) as sole 2xx response and NO phantom 200
        var adminOrdersPostResponses = paths.GetProperty("/api/v1/admin/orders").GetProperty("post").GetProperty("responses");
        Assert.False(adminOrdersPostResponses.TryGetProperty("200", out _));
        Assert.Equal("#/components/schemas/AdminOrderCreatedView",
            adminOrdersPostResponses.GetProperty("201").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        // PatchAdminOrderRequest non-nullable vs nullable fields match runtime JsonDocument parser
        var patchOrderProps = schemas.GetProperty("PatchAdminOrderRequest").GetProperty("properties");
        Assert.Equal("string", patchOrderProps.GetProperty("rowVersion").GetProperty("type").GetString());
        Assert.Equal("array", patchOrderProps.GetProperty("lines").GetProperty("type").GetString());
        Assert.Equal("integer", patchOrderProps.GetProperty("discountVnd").GetProperty("type").GetString());
        Assert.Equal("object", patchOrderProps.GetProperty("shipping").GetProperty("type").GetString());
        foreach (var nullableField in new[] { "discountNote", "shippingFeeVnd", "internalNote" })
            Assert.Contains("null", patchOrderProps.GetProperty(nullableField).GetProperty("type").EnumerateArray().Select(x => x.GetString()));
        var patchShippingProps = patchOrderProps.GetProperty("shipping").GetProperty("properties");
        foreach (var nonNullShippingField in new[] { "receiverName", "receiverPhone", "provinceCode", "addressLine" })
            Assert.Equal("string", patchShippingProps.GetProperty(nonNullShippingField).GetProperty("type").GetString());
        foreach (var nullableShippingField in new[] { "wardCode", "wardNameText", "customerNote" })
            Assert.Contains("null", patchShippingProps.GetProperty(nullableShippingField).GetProperty("type").EnumerateArray().Select(x => x.GetString()));

        foreach (var name in new[] { "AdminOrderListItemView", "AdminOrderCreatedView", "StoreProvinceListView", "StoreWardListView", "AdministrativeProvinceItem", "AdministrativeWardItem" })
        {
            var props = schemas.GetProperty(name).GetProperty("properties");
            Assert.All(props.EnumerateObject(), prop =>
            {
                Assert.DoesNotContain("cost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("profit", prop.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        // AdminOrderCreatedView (stored in idempotency_keys) has 0 PII fields
        var createdOrderProps = schemas.GetProperty("AdminOrderCreatedView").GetProperty("properties");
        Assert.Equal(18, createdOrderProps.EnumerateObject().Count());
        foreach (var piiField in new[] { "contactName", "contactPhone", "contactEmail", "receiverName", "receiverPhone", "addressLine", "customerNote", "internalNote", "discountNote" })
            Assert.False(createdOrderProps.TryGetProperty(piiField, out _));

        // AdminOrderLineView.unitCostVnd is optional (omitted when actor lacks costs.read)
        var orderLineSchema = schemas.GetProperty("AdminOrderLineView");
        Assert.True(orderLineSchema.GetProperty("properties").TryGetProperty("unitCostVnd", out _));
        if (orderLineSchema.TryGetProperty("required", out var lineRequired))
            Assert.DoesNotContain("unitCostVnd", lineRequired.EnumerateArray().Select(x => x.GetString()));

        AssertOpenApiEnum(schemas.GetProperty("CreateAdminOrderRequest").GetProperty("properties").GetProperty("salesChannel"),
            "zalo_personal", "zalo_oa", "facebook", "phone", "in_person", "other");
        AssertOpenApiEnum(schemas.GetProperty("CreateAdminOrderRequest").GetProperty("properties").GetProperty("paymentMethod"),
            "cod", "bank_transfer");
        Assert.Equal("integer", schemas.GetProperty("AdministrativeProvinceItem").GetProperty("properties").GetProperty("sortOrder").GetProperty("type").GetString());
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
