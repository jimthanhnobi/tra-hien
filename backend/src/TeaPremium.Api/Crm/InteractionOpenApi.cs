using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Crm;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Api.Crm;

public static class InteractionOpenApi
{
    private static readonly string[] CustomerGroups = ["retail", "wholesale", "reseller"];
    private static readonly string[] ActiveFollowUpContactStatuses = ["active", "do_not_contact"];

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;

        foreach (var requestName in new[]
        {
            nameof(CreateCustomerInteractionRequest),
            nameof(PatchCustomerInteractionRequest),
            nameof(CompleteFollowUpRequest)
        })
        {
            if (schemas.TryGetValue(requestName, out var raw) && raw is OpenApiSchema schema)
            {
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        ConfigureSchema(
            schemas,
            nameof(CreateCustomerInteractionRequest),
            ["channel", "kind", "content"],
            ["channel", "kind", "content"]);
        SetEnum(schemas, nameof(CreateCustomerInteractionRequest), "channel", InteractionRules.ChannelValues, nullable: false);
        SetEnum(schemas, nameof(CreateCustomerInteractionRequest), "kind", InteractionRules.ManualKindValues, nullable: false);
        SetDateTimeFormat(schemas, nameof(CreateCustomerInteractionRequest), "occurredAt", nullable: true);
        SetDateTimeFormat(schemas, nameof(CreateCustomerInteractionRequest), "followUpAt", nullable: true);

        ConfigureSchema(
            schemas,
            nameof(PatchCustomerInteractionRequest),
            [],
            ["channel", "kind", "content", "occurredAt"],
            "Partial update (at least one field required). Omitted fields are preserved; null clears orderId or uncompleted followUpAt. Rows with kind=campaign_message are immutable on PATCH.");
        SetEnum(schemas, nameof(PatchCustomerInteractionRequest), "channel", InteractionRules.ChannelValues, nullable: false);
        SetEnum(schemas, nameof(PatchCustomerInteractionRequest), "kind", InteractionRules.ManualKindValues, nullable: false);
        SetDateTimeFormat(schemas, nameof(PatchCustomerInteractionRequest), "occurredAt", nullable: false);
        SetDateTimeFormat(schemas, nameof(PatchCustomerInteractionRequest), "followUpAt", nullable: true);

        ConfigureSchema(
            schemas,
            nameof(CompleteFollowUpRequest),
            ["result"],
            ["result"]);

        // Read views: expose all 7 kinds (including system-created campaign_message).
        SetEnum(schemas, nameof(CustomerInteractionView), "channel", InteractionRules.ChannelValues, nullable: false);
        SetEnum(schemas, nameof(CustomerInteractionView), "kind", InteractionRules.AllKindValues, nullable: false);

        SetEnum(schemas, nameof(FollowUpItemView), "channel", InteractionRules.ChannelValues, nullable: false);
        SetEnum(schemas, nameof(FollowUpItemView), "kind", InteractionRules.AllKindValues, nullable: false);
        SetEnum(schemas, nameof(FollowUpItemView), "customerGroup", CustomerGroups, nullable: false);
        SetEnum(schemas, nameof(FollowUpItemView), "customerContactStatus", ActiveFollowUpContactStatuses, nullable: false);

        // Query parameter enum on GET /api/v1/admin/follow-ups
        if (document.Paths.TryGetValue("/api/v1/admin/follow-ups", out var followUpsPath)
            && followUpsPath.Operations is not null
            && followUpsPath.Operations.TryGetValue(HttpMethod.Get, out var followUpsOp)
            && followUpsOp.Parameters is not null)
        {
            var dueParam = followUpsOp.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, "due", StringComparison.Ordinal)) as OpenApiParameter;
            if (dueParam is not null)
            {
                dueParam.Required = false;
                dueParam.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = InteractionRules.FollowUpDueValues.Select(v => (JsonNode)JsonValue.Create(v)!).ToList()
                };
            }
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

    private static void SetDateTimeFormat(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        bool nullable)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
        {
            return;
        }

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        copy.Format = "date-time";
        schema.Properties[field] = copy;
    }
}
