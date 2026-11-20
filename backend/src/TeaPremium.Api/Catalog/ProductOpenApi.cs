using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TeaPremium.Api.Catalog;

public static class ProductOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        // Raw JSON binding preserves omission; the helper DTOs' CLR nullable types must not
        // advertise explicit null for nonnullable aggregate properties or required controls.
        Nonnullable(document.Components!.Schemas![nameof(ProductWriteRequest)], ["name", "productTypeId", "slug", "seoKeepAfterSold", "isFeatured", "robotsNoindex", "categoryIds", "relatedProductIds", "attributes", "defaultVariant"]);
        Nonnullable(document.Components.Schemas![nameof(VariantWriteRequest)], ["sku", "position", "isUniquePiece", "lowStockThreshold", "status", "attributes"]);
        Nonnullable(document.Components.Schemas![nameof(ProductMediaRequest)], ["assetId", "altText", "sortOrder"]);
        Set(document, "/api/v1/admin/products", HttpMethod.Post, nameof(ProductWriteRequest), "ProductCreate", ["name", "productTypeId"], ["rowVersion", "confirmTypeChange"]);
        Set(document, "/api/v1/admin/products/{id}", HttpMethod.Patch, nameof(ProductWriteRequest), "ProductPatch", ["rowVersion"], ["defaultVariant"]);
        Set(document, "/api/v1/admin/products/{id}/variants", HttpMethod.Post, nameof(VariantWriteRequest), "VariantCreate", ["rowVersion"], []);
        Set(document, "/api/v1/admin/products/{id}/variants/{childId}", HttpMethod.Patch, nameof(VariantWriteRequest), "VariantPatch", ["rowVersion"], []);
        Set(document, "/api/v1/admin/products/{id}/media", HttpMethod.Post, nameof(ProductMediaRequest), "ProductMediaCreate", ["rowVersion", "assetId", "altText"], []);
        var import = document.Paths["/api/v1/admin/products/import"].Operations![HttpMethod.Post];
        if (import.Parameters!.Single(x => x.Name == "dryRun") is OpenApiParameter mode)
        {
            mode.Required = true;
            mode.Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean };
        }
        if (import.RequestBody is OpenApiRequestBody body) body.Required = true;
        return Task.CompletedTask;
    }
    private static void Nonnullable(IOpenApiSchema schema, string[] fields)
    {
        foreach (var name in fields)
        {
            var copy = (OpenApiSchema)schema.Properties![name].CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.Null;
            // Nullable references (e.g. defaultVariant) are emitted as oneOf(null,$ref),
            // not a type flag. Clone the union list rather than mutate shared schemas.
            if (copy.OneOf is { Count: > 0 }) copy.OneOf = copy.OneOf.Where(x => x.Type != JsonSchemaType.Null).ToList();
            if (copy.AnyOf is { Count: > 0 }) copy.AnyOf = copy.AnyOf.Where(x => x.Type != JsonSchemaType.Null).ToList();
            schema.Properties[name] = copy;
        }
    }
    private static void Set(OpenApiDocument document, string path, HttpMethod method, string source, string name, string[] required, string[] excluded)
    {
        var original = document.Components!.Schemas![source];
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = original.Properties!.Where(x => !excluded.Contains(x.Key, StringComparer.Ordinal)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            Required = required.ToHashSet(StringComparer.Ordinal),
            AdditionalPropertiesAllowed = false,
            Description = "Omitted fields are preserved on partial updates. rowVersion is opaque; null clears nullable fields."
        };
        Nonnullable(schema, required);
        document.AddComponent(name, schema);
        document.Paths[path].Operations![method].RequestBody!.Content!["application/json"].Schema = new OpenApiSchemaReference(name, document);
    }
}
