using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Api.Catalog;

public record CatalogWriteRequest(string? Name = null, string? Slug = null, string? Status = null,
    string? FocusKeyword = null, string? SeoTitle = null, string? SeoDescription = null,
    bool? RobotsNoindex = null, string? NoindexReason = null, string? RowVersion = null);
public sealed record CategoryWriteRequest : CatalogWriteRequest
{
    public Guid? ParentId { get; init; }
    public int? SortOrder { get; init; }
    public Guid? CoverMediaId { get; init; }
    public string? DescriptionHtml { get; init; }
    public string? BodyHtml { get; init; }
    public JsonElement? BodyJson { get; init; }
}
public sealed record CollectionWriteRequest : CatalogWriteRequest
{
    public int? SortOrder { get; init; }
    public Guid? CoverMediaId { get; init; }
    public string? DescriptionHtml { get; init; }
    public string? BodyHtml { get; init; }
    public JsonElement? BodyJson { get; init; }
}
public sealed record MaterialWriteRequest : CatalogWriteRequest
{
    public string? Kind { get; init; }
    public string? SkuCode { get; init; }
    public string? Summary { get; init; }
    public int? SortOrder { get; init; }
    public Guid? CoverMediaId { get; init; }
    public string? DescriptionHtml { get; init; }
    public JsonElement? DescriptionJson { get; init; }
}
public sealed record ArtisanWriteRequest : CatalogWriteRequest
{
    public string? Title { get; init; }
    public Guid? PortraitMediaId { get; init; }
    public string? BioHtml { get; init; }
    public JsonElement? BioJson { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPublishRequest(string RowVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CollectionProductsRequest(string RowVersion, IReadOnlyList<CollectionProductView> Items);

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder endpoints)
    {
        Map<CategoryWriteRequest>(endpoints, "categories", CatalogKind.Category);
        Map<CollectionWriteRequest>(endpoints, "collections", CatalogKind.Collection);
        Map<MaterialWriteRequest>(endpoints, "materials", CatalogKind.Material);
        Map<ArtisanWriteRequest>(endpoints, "artisans", CatalogKind.Artisan);
        var collections = endpoints.MapGroup("/api/v1/admin/collections").WithTags("Catalog").RequireAuthorization(AdminSecurity.Ready);
        collections.MapGet("/{id:guid}/products", async (Guid id, HttpContext http, IQueryHandler<GetCollectionProducts, CollectionProductList> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.read").Produces<CollectionProductList>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        collections.MapPut("/{id:guid}/products", async (Guid id, CollectionProductsRequest request, HttpContext http, ICommandHandler<PutCollectionProducts, CollectionProductList> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id, request.RowVersion, request.Items, Actor(http)), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.write").Produces<CollectionProductList>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }
    private static void Map<TWrite>(IEndpointRouteBuilder endpoints, string resource, CatalogKind kind) where TWrite : notnull
    {
        var path = "/api/v1/admin/" + resource;
        var group = endpoints.MapGroup(path).WithTags("Catalog").RequireAuthorization(AdminSecurity.Ready);
        group.MapGet("", async (HttpContext http, IQueryHandler<ListCatalog, CatalogPage> handler, CancellationToken ct,
            int page = 1, int pageSize = 20, string? q = null, string? status = null) =>
        {
            var result = await handler.Handle(new(kind, page, pageSize, q, status), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.read").Produces<CatalogPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IQueryHandler<GetCatalog, CatalogView> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(kind, id), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.read").Produces<CatalogView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        group.MapPost("", async (HttpContext http, ICommandHandler<SaveCatalog, CatalogView> handler, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(http, ct);
            if (!body.IsSuccess) return body.Error.ToProblem(http);
            var result = await handler.Handle(new(kind, null, body.Value, Actor(http)), ct); return result.IsSuccess ? Results.Created(path + "/" + result.Value.Id, result.Value) : result.Error.ToProblem(http);
        }).Accepts<TWrite>("application/json").RequireAuthorization("catalog.write").Produces<CatalogView>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);
        group.MapPatch("/{id:guid}", async (Guid id, HttpContext http, ICommandHandler<SaveCatalog, CatalogView> handler, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(http, ct);
            if (!body.IsSuccess) return body.Error.ToProblem(http);
            var result = await handler.Handle(new(kind, id, body.Value, Actor(http)), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).Accepts<TWrite>("application/json").RequireAuthorization("catalog.write").Produces<CatalogView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        if (kind is CatalogKind.Material or CatalogKind.Artisan)
            group.MapPost("/{id:guid}/publish", async (Guid id, CatalogPublishRequest request, HttpContext http, ICommandHandler<PublishCatalog, CatalogPublication> handler, CancellationToken ct) =>
            {
                var result = await handler.Handle(new(kind, id, request.RowVersion, Actor(http)), ct); return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
            }).RequireAuthorization("catalog.publish").Produces<CatalogPublication>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
    }
    private static async Task<Result<JsonElement>> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) return Result.Failure<JsonElement>(new("validation_failed", "Yêu cầu phải là JSON.", ErrorType.Validation));
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            return Result.Success(document.RootElement.Clone());
        }
        catch (JsonException) { return Result.Failure<JsonElement>(new("validation_failed", "JSON không hợp lệ.", ErrorType.Validation)); }
    }
    private static CatalogActor Actor(HttpContext http) => new(Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!), http.User.FindAll("permission").Select(x => x.Value).ToArray());

    private const string Root = "/api/v1/admin/product-types";
    public static void MapCatalogReference(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Root).WithTags("Catalog reference").RequireAuthorization(AdminSecurity.Ready);
        group.MapGet("", async (HttpContext http, IQueryHandler<ListProductTypes, CatalogPage<ProductTypeView>> handler, CancellationToken ct, int page = 1, int pageSize = 20)
            => Reply(await handler.Handle(new(page, pageSize), ct), http))
            .RequireAuthorization("catalog.read").Produces<CatalogPage<ProductTypeView>>().ReadErrors();
        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IQueryHandler<GetProductType, ProductTypeView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id), ct), http))
            .RequireAuthorization("catalog.read").Produces<ProductTypeView>().ReadErrors();
        group.MapPost("", async (ProductTypeInput request, HttpContext http, ICommandHandler<CreateProductType, ProductTypeView> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(request), ct);
            return result.IsSuccess ? Results.Created($"{Root}/{result.Value.Id}", result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.write").Produces<ProductTypeView>(201).WriteErrors();
        group.MapPatch("/{id:guid}", async (Guid id, JsonElement request, HttpContext http, ICommandHandler<PatchProductType, ProductTypeView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id, request), ct), http))
            .Accepts<ProductTypeInput>("application/json").RequireAuthorization("catalog.write").Produces<ProductTypeView>().WriteErrors();
        group.MapGet("/{id:guid}/attributes", async (Guid id, HttpContext http, IQueryHandler<ListTypeAttributes, CatalogPage<AttributeView>> handler, CancellationToken ct, int page = 1, int pageSize = 20)
            => Reply(await handler.Handle(new(id, page, pageSize), ct), http))
            .RequireAuthorization("catalog.read").Produces<CatalogPage<AttributeView>>().ReadErrors();
        group.MapGet("/{id:guid}/attributes/{attrId:guid}", async (Guid id, Guid attrId, HttpContext http, IQueryHandler<GetTypeAttributeQuery, AttributeView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id, attrId), ct), http))
            .RequireAuthorization("catalog.read").Produces<AttributeView>().ReadErrors();
        group.MapPost("/{id:guid}/attributes", async (Guid id, AttributeInput request, HttpContext http, ICommandHandler<CreateTypeAttributeCommand, AttributeView> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id, request), ct);
            return result.IsSuccess ? Results.Created($"{Root}/{id}/attributes/{result.Value.Id}", result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.write").Produces<AttributeView>(201).WriteErrors();
        group.MapPatch("/{id:guid}/attributes/{attrId:guid}", async (Guid id, Guid attrId, JsonElement request, HttpContext http, ICommandHandler<PatchTypeAttributeCommand, AttributeView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id, attrId, request), ct), http))
            .Accepts<AttributeInput>("application/json").RequireAuthorization("catalog.write").Produces<AttributeView>().WriteErrors();
        group.MapGet("/{id:guid}/attributes/{attrId:guid}/options", async (Guid id, Guid attrId, HttpContext http, IQueryHandler<ListAttributeOptions, CatalogPage<OptionView>> handler, CancellationToken ct, int page = 1, int pageSize = 20)
            => Reply(await handler.Handle(new(id, attrId, page, pageSize), ct), http))
            .RequireAuthorization("catalog.read").Produces<CatalogPage<OptionView>>().ReadErrors();
        group.MapGet("/{id:guid}/attributes/{attrId:guid}/options/{optId:guid}", async (Guid id, Guid attrId, Guid optId, HttpContext http, IQueryHandler<GetAttributeOption, OptionView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id, attrId, optId), ct), http))
            .RequireAuthorization("catalog.read").Produces<OptionView>().ReadErrors();
        group.MapPost("/{id:guid}/attributes/{attrId:guid}/options", async (Guid id, Guid attrId, OptionInput request, HttpContext http, ICommandHandler<CreateAttributeOption, OptionView> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id, attrId, request), ct);
            return result.IsSuccess ? Results.Created($"{Root}/{id}/attributes/{attrId}/options/{result.Value.Id}", result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.write").Produces<OptionView>(201).WriteErrors();
        group.MapPatch("/{id:guid}/attributes/{attrId:guid}/options/{optId:guid}", async (Guid id, Guid attrId, Guid optId, JsonElement request, HttpContext http, ICommandHandler<PatchAttributeOption, OptionView> handler, CancellationToken ct)
            => Reply(await handler.Handle(new(id, attrId, optId, request), ct), http))
            .Accepts<OptionInput>("application/json").RequireAuthorization("catalog.write").Produces<OptionView>().WriteErrors();
    }
    private static IResult Reply<T>(Result<T> result, HttpContext http)
        => result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    private static RouteHandlerBuilder ReadErrors(this RouteHandlerBuilder builder)
        => builder.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    private static RouteHandlerBuilder WriteErrors(this RouteHandlerBuilder builder)
        => builder.ReadErrors().ProducesProblem(409).ProducesProblem(422);
}
