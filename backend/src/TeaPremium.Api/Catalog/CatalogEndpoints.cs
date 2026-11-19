using System.Text.Json;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Catalog;

public static class CatalogEndpoints
{
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
