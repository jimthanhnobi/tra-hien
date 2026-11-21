using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Catalog;

namespace TeaPremium.Api.Catalog;

internal static class StoreCatalogOpenApi
{
    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var products = document.Paths["/api/v1/store/products"].Operations![HttpMethod.Get];
        var parameters = products.Parameters!;
        var sort = (OpenApiParameter)parameters.Single(parameter => parameter.Name == "sort");
        sort.Schema = Strings("newest", "price_asc", "price_desc");

        // Form + explode serializes object properties as distinct query parameters.
        // The parameter name is descriptive; the wire key is e.g. attr.capacity.
        parameters.Add(new OpenApiParameter
        {
            Name = "attributes",
            In = ParameterLocation.Query,
            Style = ParameterStyle.Form,
            Explode = true,
            Description = "Flat attr.<code> filters. Send for example attr.capacity=240-260.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                AdditionalPropertiesAllowed = false,
                PatternProperties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    [@"^attr\.[a-z0-9_]{2,40}$"] = new OpenApiSchema { Type = JsonSchemaType.String }
                }
            }
        });

        var schemas = document.Components!.Schemas!;
        foreach (var name in new[] { nameof(StoreProductCard), nameof(StoreVariant) })
            schemas[name].Properties!["availability"] = Strings("in_stock", "out_of_stock", "sold");

        return Task.CompletedTask;
    }

    private static OpenApiSchema Strings(params string[] values) => new()
    {
        Type = JsonSchemaType.String,
        Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
    };
}
