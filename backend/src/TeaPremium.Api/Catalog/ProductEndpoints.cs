using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Catalog;

// OpenAPI input shapes document partial writes. Raw JSON binding preserves omission vs explicit null.
public class ProductWriteRequest
{
    public string? RowVersion { get; init; }
    public Guid? ProductTypeId { get; init; }
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Subtitle { get; init; }
    public string? ShortDescription { get; init; }
    public string? DescriptionHtml { get; init; }
    public JsonElement? DescriptionJson { get; init; }
    public string? CareHtml { get; init; }
    public Guid? PrimaryCategoryId { get; init; }
    public Guid? MaterialId { get; init; }
    public Guid? ArtisanId { get; init; }
    public string? OriginNote { get; init; }
    public bool? SeoKeepAfterSold { get; init; }
    public bool? IsFeatured { get; init; }
    public string? FocusKeyword { get; init; }
    public string? SeoTitle { get; init; }
    public string? SeoDescription { get; init; }
    public string? CanonicalUrl { get; init; }
    public bool? RobotsNoindex { get; init; }
    public string? NoindexReason { get; init; }
    public Guid? OgMediaId { get; init; }
    public Guid[]? CategoryIds { get; init; }
    public Guid[]? RelatedProductIds { get; init; }
    public ProductValue[]? Attributes { get; init; }
    public bool? ConfirmTypeChange { get; init; }
    public VariantWriteRequest? DefaultVariant { get; init; }
}
public sealed class VariantWriteRequest
{
    public string? RowVersion { get; init; }
    public string? Sku { get; init; }
    public string? Name { get; init; }
    public int? Position { get; init; }
    public long? PriceVnd { get; init; }
    public bool? IsUniquePiece { get; init; }
    public int? WeightG { get; init; }
    public JsonElement? PackageDimsCm { get; init; }
    public string? Barcode { get; init; }
    public int? LowStockThreshold { get; init; }
    public string? Status { get; init; }
    public ProductValue[]? Attributes { get; init; }
}
public sealed record ProductStateRequest(string RowVersion);
public sealed record ProductArchiveRequest(string RowVersion, string? RedirectTo = null);
public sealed record ProductMediaRequest(string RowVersion, Guid? AssetId = null, Guid? VariantId = null, string? AltText = null, int? SortOrder = null);
public sealed record ProductMediaOrderRequest(string RowVersion, Guid[] MediaIds);

public static class ProductEndpoints
{
    private const string Root = "/api/v1/admin/products";
    private static readonly JsonSerializerOptions RestrictedInventoryJson = CreateRestrictedInventoryJson();
    public static void MapProducts(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Root).WithTags("Products").RequireAuthorization(AdminSecurity.Ready);
        group.MapImport();
        group.MapGet("", async (HttpContext http, IQueryHandler<ListProducts, CatalogPage<ProductData>> handler, CancellationToken ct,
            int page = 1, int pageSize = 20, string? q = null, string? status = null, Guid? type = null, Guid? category = null, Guid? material = null, Guid? artisan = null, string? stock = null) =>
        {
            var result = await handler.Handle(new(page, pageSize, q, status, type, category, material, artisan, stock, Actor(http)), ct);
            return result.IsSuccess ? ProductResponse(http, result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.read").Produces<CatalogPage<ProductData>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        group.MapGet("/{id:guid}", ReadProduct).RequireAuthorization("catalog.read").Produces<ProductData>().ReadErrors();
        group.MapGet("/{id:guid}/variants", async (Guid id, HttpContext http, IQueryHandler<GetProduct, ProductData> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id), ct); return result.IsSuccess ? ProductResponse(http, result.Value.Variants) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.write").Produces<List<VariantData>>().ReadErrors();
        group.MapGet("/{id:guid}/variants/{childId:guid}", async (Guid id, Guid childId, HttpContext http, IQueryHandler<GetProduct, ProductData> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id), ct); if (!result.IsSuccess) return result.Error.ToProblem(http);
            var v = result.Value.Variants.SingleOrDefault(x => x.Id == childId);
            return v is null ? new TeaPremium.Application.Results.Error("not_found", "Không tìm thấy biến thể.", TeaPremium.Application.Results.ErrorType.NotFound).ToProblem(http) : ProductResponse(http, v);
        }).RequireAuthorization("catalog.write").Produces<VariantData>().ReadErrors();
        Write<ProductWriteRequest>(group, "", "product", HttpMethod.Post, "catalog.write", true);
        Write<ProductWriteRequest>(group, "/{id:guid}", "product", HttpMethod.Patch, "catalog.write");
        Write<VariantWriteRequest>(group, "/{id:guid}/variants", "variant", HttpMethod.Post, "catalog.write");
        Write<VariantWriteRequest>(group, "/{id:guid}/variants/{childId:guid}", "variant", HttpMethod.Patch, "catalog.write");
        Write<ProductStateRequest>(group, "/{id:guid}/publish", "publish", HttpMethod.Post, "catalog.publish");
        Write<ProductArchiveRequest>(group, "/{id:guid}/archive", "archive", HttpMethod.Post, "catalog.publish");
        Write<ProductStateRequest>(group, "/{id:guid}/restore-draft", "restore-draft", HttpMethod.Post, "catalog.publish");
        Write<ProductMediaRequest>(group, "/{id:guid}/media", "attach-media", HttpMethod.Post, "catalog.write");
        Write<ProductMediaRequest>(group, "/{id:guid}/media/{childId:guid}", "patch-media", HttpMethod.Patch, "catalog.write");
        Write<ProductStateRequest>(group, "/{id:guid}/media/{childId:guid}", "delete-media", HttpMethod.Delete, "catalog.write");
        Write<ProductStateRequest>(group, "/{id:guid}/media/{childId:guid}/cover", "cover", HttpMethod.Post, "catalog.write");
        Write<ProductMediaOrderRequest>(group, "/{id:guid}/media/order", "order", HttpMethod.Put, "catalog.write");
    }
    private static async Task<IResult> ReadProduct(Guid id, HttpContext http, IQueryHandler<GetProduct, ProductData> handler, CancellationToken ct)
    { var result = await handler.Handle(new(id), ct); return result.IsSuccess ? ProductResponse(http, result.Value) : result.Error.ToProblem(http); }
    private static void Write<T>(RouteGroupBuilder group, string path, string operation, HttpMethod method, string permission, bool create = false) where T : notnull
    {
        group.MapMethods(path, [method.Method], async (HttpContext http, ICommandHandler<ProductMutation, ProductData> handler, CancellationToken ct) =>
        {
            if (!http.Request.HasJsonContentType()) return ProductRules.Bad("body").ToProblem(http);
            JsonElement fields;
            try { using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct); fields = document.RootElement.Clone(); }
            catch (JsonException) { return ProductRules.Bad("body").ToProblem(http); }
            Guid? id = Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var productId) ? productId : null;
            Guid? child = Guid.TryParse(http.Request.RouteValues["childId"]?.ToString(), out var childId) ? childId : null;
            var result = await handler.Handle(new(id, operation, child, fields, Actor(http)), ct);
            if (!result.IsSuccess) return result.Error.ToProblem(http);
            return ProductResponse(http, result.Value, create ? Root + "/" + result.Value.Id : null);
        }).Accepts<T>("application/json").RequireAuthorization(permission).Produces<ProductData>(create ? 201 : 200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
    }
    private static IResult ProductResponse<T>(HttpContext http, T value, string? location = null)
    {
        if (!http.User.HasClaim("permission", "inventory.read") || http.User.HasClaim("permission", "inventory.write"))
            return location is null ? Results.Ok(value) : Results.Created(location, value);

        // The threshold combined with binary availability would reveal low status for out-of-stock variants.
        if (location is not null) http.Response.Headers.Location = location;
        return Results.Json(value, RestrictedInventoryJson, statusCode: location is null ? 200 : 201);
    }

    private static JsonSerializerOptions CreateRestrictedInventoryJson()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(VariantData))
                type.Properties.Single(x => x.Name == "lowStockThreshold").ShouldSerialize = (_, _) => false;
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    internal static ProductActor Actor(HttpContext http) => new(Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        http.User.FindAll("permission").Select(x => x.Value).ToArray(), http.User.IsInRole("owner"));
    private static RouteHandlerBuilder ReadErrors(this RouteHandlerBuilder builder) => builder.ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
}
