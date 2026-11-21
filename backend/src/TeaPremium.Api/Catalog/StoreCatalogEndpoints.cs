using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;

namespace TeaPremium.Api.Catalog;

public static class StoreCatalogEndpoints
{
    public static void MapStoreCatalog(this IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.MapGroup("/api/v1/store").WithTags("Store catalog").AllowAnonymous();
        store.MapGet("/products", Products)
            .WithDescription("Filter attributes with attr.<code>=<optionCode> or attr.<code>=<min>-<max>. Use one value per key. Variant predicates match the same eligible variant. Facets count distinct products before pagination.")
            .Produces<StoreProductPage>().ProducesProblem(400);
        store.MapGet("/products/{slug}", async (string slug, HttpContext http, IStoreCatalogRead read, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Find(await read.ProductAsync(slug, ct), http);
        }).Produces<StoreProductDetail>().ProducesProblem(404);
        store.MapGet("/navigation", (IStoreCatalogRead read, CancellationToken ct) => read.NavigationAsync(ct));
        MapTaxonomy(store, "categories", StoreTaxonomy.Category, false);
        MapTaxonomy(store, "collections", StoreTaxonomy.Collection, false);
        MapTaxonomy(store, "materials", StoreTaxonomy.Material, true);
        MapTaxonomy(store, "artisans", StoreTaxonomy.Artisan, true);
    }

    // String bindings describe the public query in OpenAPI. The parser also validates repeated keys
    // and collects the dynamically named attr.<code> parameters.
    private static async Task<IResult> Products(HttpContext http, IStoreCatalogRead read,
        string? category, string? collection, string? type, string? material, string? artisan,
        string? priceMin, string? priceMax, string? inStock, string? q, string? sort, string? page, string? pageSize, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var query = StoreCatalogQueryParser.Parse(http.Request.Query.ToDictionary(
            x => x.Key, x => x.Value.Select(value => value ?? "").ToArray(), StringComparer.OrdinalIgnoreCase));
        var result = query is null ? null : await read.ProductsAsync(query, ct);
        return result is null
            ? new Error("validation_failed", "Bộ lọc danh mục không hợp lệ.", ErrorType.Validation).ToProblem(http)
            : Results.Ok(result);
    }

    private static void MapTaxonomy(RouteGroupBuilder store, string path, StoreTaxonomy kind, bool list)
    {
        if (list)
            store.MapGet("/" + path, (IStoreCatalogRead read, CancellationToken ct) => read.TaxonomyListAsync(kind, ct));
        store.MapGet("/" + path + "/{slug}", async (string slug, HttpContext http, IStoreCatalogRead read, CancellationToken ct) =>
            Find(await read.TaxonomyAsync(kind, slug, ct), http))
            .Produces<StoreTaxonomyDetail>().ProducesProblem(404);
    }

    private static IResult Find<T>(T? result, HttpContext http) where T : class => result is null
        ? new Error("not_found", "Không tìm thấy nội dung.", ErrorType.NotFound).ToProblem(http)
        : Results.Ok(result);
}
