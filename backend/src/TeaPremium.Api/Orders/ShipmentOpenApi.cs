using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Orders;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Api.Orders;

public static class ShipmentOpenApi
{
    private static readonly string[] OrderStatuses =
    [
        OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipping,
        OrderStatus.Delivered, OrderStatus.DeliveryFailed, OrderStatus.Cancelled
    ];

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;

        foreach (var requestName in new[]
        {
            nameof(CreateOrderShipmentRequest),
            nameof(PatchShipmentRequest),
            nameof(RecordShipmentEventRequest)
        })
        {
            if (schemas.TryGetValue(requestName, out var raw) && raw is OpenApiSchema schema)
            {
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        ConfigureRequired(schemas, nameof(CreateOrderShipmentRequest), ["carrierName"], ["carrierName"]);
        SetStrictMinimum(schemas, nameof(CreateOrderShipmentRequest), "shippingCostVnd", "0");

        ConfigureRequired(schemas, nameof(PatchShipmentRequest), [], []);
        SetStrictMinimum(schemas, nameof(PatchShipmentRequest), "shippingCostVnd", "0");
        SetStrictMinimum(schemas, nameof(PatchShipmentRequest), "returnCostVnd", "0");

        ConfigureRequired(schemas, nameof(RecordShipmentEventRequest), ["status", "occurredAt"], ["status", "occurredAt"]);
        SetEnum(schemas, nameof(RecordShipmentEventRequest), "status", ShipmentStatus.EventRecordable, nullable: false);
        SetStrictMinimum(schemas, nameof(RecordShipmentEventRequest), "returnCostVnd", "0");

        SetEnum(schemas, nameof(ShipmentDetailView), "orderStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(ShipmentDetailView), "purpose", ShipmentPurpose.All, nullable: false);
        SetEnum(schemas, nameof(ShipmentDetailView), "provider", ShippingProviderCode.All, nullable: false);
        SetEnum(schemas, nameof(ShipmentDetailView), "status", ShipmentStatus.All, nullable: false);
        StripStringNumericType(schemas, nameof(ShipmentDetailView), "codAmountVnd", "shippingCostVnd", "returnCostVnd");

        SetEnum(schemas, nameof(ShipmentEventView), "status", ShipmentStatus.All, nullable: false);
        SetEnum(schemas, nameof(ShipmentEventView), "source", ShipmentEventSource.All, nullable: false);

        return Task.CompletedTask;
    }

    private static void ConfigureRequired(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string[] required,
        string[] nonNullable)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        schema.Required = required.ToHashSet(StringComparer.Ordinal);
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

    private static void SetStrictMinimum(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        string minimum)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
            return;

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type &= ~JsonSchemaType.String;
        copy.Pattern = null;
        copy.Minimum = minimum;
        schema.Properties[field] = copy;
    }

    private static void StripStringNumericType(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        foreach (var field in fields)
        {
            if (!schema.Properties.TryGetValue(field, out var prop))
                continue;
            var copy = (OpenApiSchema)prop.CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.String;
            copy.Pattern = null;
            schema.Properties[field] = copy;
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
            return;

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        copy.Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList();
        schema.Properties[field] = copy;
    }
}
