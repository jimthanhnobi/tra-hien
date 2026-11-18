using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Settings;

namespace TeaPremium.Api.Settings;

public static class SettingsOpenApi
{
    private const string ValueSchema = "SettingsJsonValue";

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        // JsonElement has no exported shape. Describe JSON recursively rather than
        // emitting an empty object/unknown, which produces Record<string, never>.
        document.AddComponent(ValueSchema, new OpenApiSchema
        {
            AnyOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.Null },
                new OpenApiSchema { Type = JsonSchemaType.Boolean },
                new OpenApiSchema { Type = JsonSchemaType.Number },
                new OpenApiSchema { Type = JsonSchemaType.String },
                new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchemaReference(ValueSchema, document) },
                Values(document)
            ]
        });
        foreach (var name in new[] { nameof(SettingsGroup), nameof(UpdateSettingsRequest) })
            document.Components!.Schemas![name].Properties!["values"] = Values(document);
        return Task.CompletedTask;
    }

    private static OpenApiSchema Values(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalProperties = new OpenApiSchemaReference(ValueSchema, document)
    };
}
