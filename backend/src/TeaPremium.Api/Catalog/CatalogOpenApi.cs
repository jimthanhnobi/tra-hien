using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Catalog;

namespace TeaPremium.Api.Catalog;

public static class CatalogOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        foreach (var (name, segment) in new[] { (nameof(ProductTypeInput), "product-types/{id}"),
            (nameof(AttributeInput), "attributes/{attrId}"), (nameof(OptionInput), "options/{optId}") })
        {
            var input = document.Components!.Schemas![name];
            // openapi-typescript treats a default as a required client field. Catalog
            // defaults belong to POST, while omission in PATCH must preserve current data.
            // Keep the default documented without turning optional fields into required ones.
            foreach (var property in input.Properties!.Values.OfType<OpenApiSchema>())
            {
                if (property.Default is not { } defaultValue) continue;
                property.Description = $"Server default on creation: {defaultValue.ToJsonString()}. Omission in PATCH preserves the current value.";
                property.Default = null;
            }
            var properties = new Dictionary<string, IOpenApiSchema>(input.Properties!, StringComparer.Ordinal)
            { ["rowVersion"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{64}$" } };
            var patchName = name.Replace("Input", "Patch", StringComparison.Ordinal);
            document.AddComponent(patchName, new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = properties,
                Required = new HashSet<string> { "rowVersion" },
                AdditionalPropertiesAllowed = false,
                Description = "Partial metadata update. Omitted fields are preserved; null clears nullable fields. rowVersion is required."
            });
            foreach (var path in document.Paths.Where(x => x.Key.EndsWith(segment, StringComparison.Ordinal)))
                if (path.Value.Operations?.TryGetValue(HttpMethod.Patch, out var operation) == true)
                    operation.RequestBody!.Content!["application/json"].Schema = new OpenApiSchemaReference(patchName, document);
        }
        return Task.CompletedTask;
    }
}
