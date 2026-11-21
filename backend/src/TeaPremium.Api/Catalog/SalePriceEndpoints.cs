using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSalePriceRequest(string RowVersion,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long SalePriceVnd, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string? Note = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteSalePriceRequest(string RowVersion);

public static class SalePriceEndpoints
{
    private const string Root = "/api/v1/admin/variants";
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        Converters = { new OffsetTimestampConverter() }
    };

    public static void MapSalePrices(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Root + "/{id:guid}/sale-prices").WithTags("SalePrices")
            .RequireAuthorization(AdminSecurity.Ready, "pricing.write");
        group.MapGet("", ListAsync).WithDescription("The list rowVersion identifies the variant. Each item has its own rowVersion.")
            .Produces<SalePriceList>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        group.MapPost("", CreateAsync).WithJsonRequest<CreateSalePriceRequest>()
            .WithDescription("Use the variant rowVersion and ISO 8601 timestamps that include a UTC offset.")
            .WithMetadata(new RequireCsrfHeader()).Produces<SalePriceView>(201).WriteErrors().ProducesProblem(422);
        group.MapDelete("/{salePriceId:guid}", DeleteAsync).WithJsonRequest<DeleteSalePriceRequest>()
            .WithDescription("Use the sale price item's rowVersion.")
            .WithMetadata(new RequireCsrfHeader()).Produces(204).WriteErrors();
    }

    private static async Task<IResult> ListAsync(Guid id, HttpContext http, IQueryHandler<ListSalePrices, SalePriceList> handler, CancellationToken ct)
    {
        var result = await handler.Handle(new(id), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    }

    private static async Task<IResult> CreateAsync(Guid id, HttpContext http, ICommandHandler<CreateSalePrice, SalePriceView> handler, CancellationToken ct)
    {
        if (UnsupportedContentType(http.Request)) return AdminSecurity.Problem(http, 415, "unsupported_media_type");
        var body = await ReadAsync<CreateSalePriceRequest>(http.Request, ct);
        if (body is null) return ProductRules.Bad("body").ToProblem(http);
        var result = await handler.Handle(new(id, body.RowVersion, body.SalePriceVnd, SalePriceTime.ToPersistedUtc(body.StartsAt),
            SalePriceTime.ToPersistedUtc(body.EndsAt), body.Note, ProductEndpoints.Actor(http)), ct);
        return result.IsSuccess ? Results.Created($"{Root}/{id}/sale-prices/{result.Value.Id}", result.Value) : result.Error.ToProblem(http);
    }

    private static async Task<IResult> DeleteAsync(Guid id, Guid salePriceId, HttpContext http, ICommandHandler<DeleteSalePrice, bool> handler, CancellationToken ct)
    {
        if (UnsupportedContentType(http.Request)) return AdminSecurity.Problem(http, 415, "unsupported_media_type");
        var body = await ReadAsync<DeleteSalePriceRequest>(http.Request, ct);
        if (body is null) return ProductRules.Bad("body").ToProblem(http);
        var result = await handler.Handle(new(id, salePriceId, body.RowVersion, ProductEndpoints.Actor(http)), ct);
        return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem(http);
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType()) return null;
        try { return await JsonSerializer.DeserializeAsync<T>(request.Body, Input, ct); }
        catch (JsonException) { return null; }
    }

    private static RouteHandlerBuilder WriteErrors(this RouteHandlerBuilder builder) =>
        builder.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(415);

    private static bool UnsupportedContentType(HttpRequest request) =>
        !string.IsNullOrEmpty(request.ContentType) &&
        (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase));

    // Document the manually bound body without .Accepts metadata, which can short-circuit routing with an empty 415.
    private static RouteHandlerBuilder WithJsonRequest<T>(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer(async (operation, context, ct) =>
        {
            var document = context.Document!;
            var schemaName = typeof(T).Name;
            document.AddComponent(schemaName, await context.GetOrCreateSchemaAsync(typeof(T), cancellationToken: ct));
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchemaReference(schemaName, document) }
                }
            };
        });

    private sealed class OffsetTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text
                && (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')
                && reader.TryGetDateTimeOffset(out var value))
                return value;
            throw new JsonException("An ISO 8601 timestamp with a UTC offset is required.");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
