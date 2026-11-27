using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Api.Crm;

public static class CustomerOpenApi
{
    private static readonly string[] CustomerGroups = ["retail", "wholesale", "reseller"];
    private static readonly string[] Origins = ["legacy_import", "web", "admin"];
    private static readonly string[] AcquisitionSources =
    [
        "unknown", "legacy", "google_organic", "google_ads", "facebook",
        "zalo_personal", "zalo_oa", "referral", "direct", "offline", "other"
    ];
    private static readonly string[] ContactStatuses = ["active", "do_not_contact", "erased"];
    private static readonly string[] WritableContactStatuses = ["active", "do_not_contact"];
    private static readonly string[] AccountStatuses = ["active", "locked", "disabled"];
    private static readonly string[] LoginProviders = ["google", "zalo"];
    private static readonly string[] OrderOrigins = ["web", "admin"];
    private static readonly string[] OrderSalesChannels = ["web", "zalo_personal", "zalo_oa", "facebook", "phone", "in_person", "other"];
    private static readonly string[] OrderStatuses = ["pending", "confirmed", "shipping", "delivered", "delivery_failed", "cancelled"];
    private static readonly string[] OrderPaymentMethods = ["cod", "bank_transfer"];
    private static readonly string[] OrderPaymentStatuses = ["unpaid", "partially_paid", "paid", "partially_refunded", "refunded"];
    private static readonly string[] SortValues =
    [
        "createdAt:desc", "createdAt:asc",
        "lastDeliveredAt:desc", "lastDeliveredAt:asc",
        "lifetimeValueVnd:desc", "lifetimeValueVnd:asc",
        "fullName:asc", "fullName:desc"
    ];

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;

        foreach (var requestName in new[]
        {
            nameof(CreateCustomerRequest),
            nameof(PatchCustomerRequest),
            nameof(CorrectCustomerIdentityRequest),
            nameof(CreateTagRequest),
            nameof(PatchTagRequest),
            nameof(PutCustomerTagsRequest),
            nameof(CreateCustomerAddressRequest),
            nameof(PatchCustomerAddressRequest),
            nameof(DeleteCustomerAddressRequest),
            nameof(PutCustomerConsentRequest),
            nameof(MergeCustomersRequest),
            nameof(EraseCustomerRequest)
        })
        {
            if (schemas.TryGetValue(requestName, out var raw) && raw is OpenApiSchema schema)
            {
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        ConfigureSchema(schemas, nameof(CreateCustomerRequest), ["fullName"], ["fullName", "isVip", "hadLegacyPurchase"]);
        SetEnum(schemas, nameof(CreateCustomerRequest), "customerGroup", CustomerGroups, nullable: true);
        SetEnum(schemas, nameof(CreateCustomerRequest), "acquisitionSource", AcquisitionSources, nullable: true);
        SetEnum(schemas, nameof(CreateCustomerRequest), "contactStatus", WritableContactStatuses, nullable: true);

        ConfigureSchema(schemas, nameof(PatchCustomerRequest), ["rowVersion"], ["rowVersion", "fullName", "customerGroup", "isVip", "contactStatus"],
            "Omitted fields are preserved on partial updates. rowVersion is opaque; null clears nullable fields.");
        SetEnum(schemas, nameof(PatchCustomerRequest), "customerGroup", CustomerGroups, nullable: false);
        SetEnum(schemas, nameof(PatchCustomerRequest), "contactStatus", WritableContactStatuses, nullable: false);

        ConfigureSchema(schemas, nameof(CorrectCustomerIdentityRequest), ["rowVersion", "reason"], ["rowVersion", "reason", "origin", "acquisitionSource", "hadLegacyPurchase"],
            "Requires customers.write for phone; owner role required for origin, acquisitionSource or hadLegacyPurchase.");
        SetEnum(schemas, nameof(CorrectCustomerIdentityRequest), "origin", Origins, nullable: false);
        SetEnum(schemas, nameof(CorrectCustomerIdentityRequest), "acquisitionSource", AcquisitionSources, nullable: false);

        ConfigureSchema(schemas, nameof(CreateTagRequest), ["name"], ["name"]);
        ConfigureSchema(schemas, nameof(PatchTagRequest), [], ["name"],
            "Omitted fields are preserved on partial updates; null clears color.");
        ConfigureSchema(schemas, nameof(PutCustomerTagsRequest), ["tagIds"], ["tagIds"]);

        ConfigureSchema(schemas, nameof(CreateCustomerAddressRequest), ["receiverName", "receiverPhone", "provinceCode", "addressLine"],
            ["receiverName", "receiverPhone", "provinceCode", "addressLine", "isDefault"]);
        ConfigureSchema(schemas, nameof(PatchCustomerAddressRequest), [], ["receiverName", "receiverPhone", "provinceCode", "addressLine", "isDefault"],
            "Omitted fields are preserved on partial updates; null clears nullable fields.");

        // P2-12 privacy request schemas (ASSUMPTION-P212-01 = A: optional rowVersion/mergeRowVersion).
        ConfigureSchema(
            schemas,
            nameof(PutCustomerConsentRequest),
            ["channel", "purpose", "status", "evidence"],
            ["channel", "purpose", "status", "evidence"],
            "Upserts staff-recorded customer consent by (channel, purpose). source is fixed to 'staff'; rowVersion is optional.");
        SetEnum(schemas, nameof(PutCustomerConsentRequest), "channel", [.. CustomerRules.ConsentChannels], nullable: false);
        SetEnum(schemas, nameof(PutCustomerConsentRequest), "purpose", [.. CustomerRules.ConsentPurposes], nullable: false);
        SetEnum(schemas, nameof(PutCustomerConsentRequest), "status", [.. CustomerRules.ConsentStatuses], nullable: false);

        ConfigureSchema(
            schemas,
            nameof(MergeCustomersRequest),
            ["mergeCustomerId"],
            ["mergeCustomerId"],
            "Merges mergeCustomerId (B) into target customer {id} (A). rowVersion and mergeRowVersion are optional.");

        ConfigureSchema(
            schemas,
            nameof(EraseCustomerRequest),
            ["reason", "requestChannel"],
            ["reason", "requestChannel"],
            "Irreversibly anonymizes customer PII and linked merged stubs while preserving order financials (AC-P2-10). rowVersion is optional.");
        SetEnum(schemas, nameof(EraseCustomerRequest), "requestChannel", [.. CustomerRules.DataRequestChannels], nullable: false);

        // Read view enums
        foreach (var viewName in new[] { nameof(CustomerSummaryView), nameof(CustomerDetailView), nameof(CustomerExportProfileView) })
        {
            SetEnum(schemas, viewName, "customerGroup", CustomerGroups, nullable: false);
            SetEnum(schemas, viewName, "origin", Origins, nullable: false);
            SetEnum(schemas, viewName, "acquisitionSource", AcquisitionSources, nullable: false);
            SetEnum(schemas, viewName, "contactStatus", ContactStatuses, nullable: false);
        }

        SetEnum(schemas, nameof(CustomerConsentItemView), "channel", [.. CustomerRules.ConsentChannels], nullable: false);
        SetEnum(schemas, nameof(CustomerConsentItemView), "purpose", [.. CustomerRules.ConsentPurposes], nullable: false);
        SetEnum(schemas, nameof(CustomerConsentItemView), "status", [.. CustomerRules.ConsentStatuses], nullable: false);
        SetEnum(schemas, nameof(CustomerConsentItemView), "source", [.. CustomerRules.ConsentSources], nullable: false);

        SetEnum(schemas, nameof(CustomerEraseResult), "contactStatus", ["erased"], nullable: false);

        SetEnum(schemas, nameof(CustomerExportAccountView), "status", AccountStatuses, nullable: false);
        SetEnum(schemas, nameof(CustomerExportLoginView), "provider", LoginProviders, nullable: false);
        SetEnum(schemas, nameof(CustomerExportInteractionView), "channel", [.. CustomerRules.InteractionChannels], nullable: false);
        SetEnum(schemas, nameof(CustomerExportInteractionView), "kind", [.. CustomerRules.InteractionKinds], nullable: false);
        SetEnum(schemas, nameof(CustomerExportDataRequestView), "requestType", [.. CustomerRules.DataRequestTypes], nullable: false);
        SetEnum(schemas, nameof(CustomerExportDataRequestView), "status", [.. CustomerRules.DataRequestStatuses], nullable: false);
        SetEnum(schemas, nameof(CustomerExportDataRequestView), "channel", [.. CustomerRules.DataRequestChannels], nullable: false);
        SetEnum(schemas, nameof(CustomerExportOrderView), "origin", OrderOrigins, nullable: false);
        SetEnum(schemas, nameof(CustomerExportOrderView), "salesChannel", OrderSalesChannels, nullable: false);
        SetEnum(schemas, nameof(CustomerExportOrderView), "status", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(CustomerExportOrderView), "paymentMethod", OrderPaymentMethods, nullable: false);
        SetEnum(schemas, nameof(CustomerExportOrderView), "paymentStatus", OrderPaymentStatuses, nullable: false);

        StripStringNumericType(schemas, nameof(CustomerEraseResult), "anonymizedOrdersCount", "scrubbedMergedStubsCount");
        StripStringNumericType(schemas, nameof(CustomerExportProfileView), "deliveredOrdersCount", "lifetimeValueVnd");
        StripStringNumericType(schemas, nameof(CustomerExportOrderView), "subtotalVnd", "discountVnd", "shippingFeeVnd", "totalVnd");
        StripStringNumericType(schemas, nameof(CustomerExportOrderLineView), "quantity", "listPriceVnd", "unitPriceVnd", "lineTotalVnd", "returnedQuantity");

        SetEnum(schemas, nameof(CustomerImportRow), "status", CustomerImportSchema.RowStatuses, nullable: false);
        SetEnum(schemas, nameof(CustomerImportRow), "reasonCode", CustomerImportSchema.ReasonCodes, nullable: false);
        SetArrayItemEnum(schemas, nameof(CustomerImportRow), "warnings", CustomerImportSchema.WarningCodes);

        // Query parameter enums on GET /api/v1/admin/customers
        if (document.Paths.TryGetValue("/api/v1/admin/customers", out var listPath)
            && listPath.Operations is not null
            && listPath.Operations.TryGetValue(HttpMethod.Get, out var listOp)
            && listOp.Parameters is not null)
        {
            SetParamEnum(listOp.Parameters, "sort", SortValues);
            SetParamEnum(listOp.Parameters, "group", CustomerGroups);
            SetParamEnum(listOp.Parameters, "contactStatus", ContactStatuses);
        }

        return Task.CompletedTask;
    }

    private static void ConfigureSchema(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string[] required,
        string[] nonNullable,
        string? description = null)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
        {
            return;
        }

        if (required.Length > 0)
        {
            schema.Required = required.ToHashSet(StringComparer.Ordinal);
        }

        if (description is not null)
        {
            schema.Description = description;
        }

        foreach (var field in nonNullable)
        {
            if (schema.Properties.TryGetValue(field, out var prop))
            {
                var copy = (OpenApiSchema)prop.CreateShallowCopy();
                copy.Type &= ~JsonSchemaType.Null;
                schema.Properties[field] = copy;
            }
        }
    }

    private static void SetEnum(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        string[] values,
        bool nullable)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
        {
            return;
        }

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        copy.Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList();
        schema.Properties[field] = copy;
    }

    private static void SetArrayItemEnum(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        string[] values)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
        {
            return;
        }

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type = JsonSchemaType.Array;
        copy.Items = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList()
        };
        schema.Properties[field] = copy;
    }

    private static void SetParamEnum(IList<IOpenApiParameter> parameters, string name, string[] values)
    {
        var param = parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) as OpenApiParameter;
        if (param is null)
        {
            return;
        }

        param.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList()
        };
    }

    private static void StripStringNumericType(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
        {
            return;
        }

        foreach (var field in fields)
        {
            if (!schema.Properties.TryGetValue(field, out var prop))
            {
                continue;
            }

            var copy = (OpenApiSchema)prop.CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.String;
            copy.Pattern = null;
            schema.Properties[field] = copy;
        }
    }
}
