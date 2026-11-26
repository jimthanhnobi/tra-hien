using System.Text.Json;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Crm;

public sealed record CreateCustomerInteractionRequest(
    string? Channel = null,
    string? Kind = null,
    string? Content = null,
    string? OccurredAt = null,
    Guid? OrderId = null,
    string? FollowUpAt = null);

public sealed class PatchCustomerInteractionRequest
{
    public string? Channel { get; init; }
    public string? Kind { get; init; }
    public string? Content { get; init; }
    public string? OccurredAt { get; init; }
    public Guid? OrderId { get; init; }
    public string? FollowUpAt { get; init; }
}

public sealed record CompleteFollowUpRequest(string? Result = null);

public static class InteractionEndpoints
{
    private const string CustomersRoot = "/api/v1/admin/customers";
    private const string FollowUpsRoot = "/api/v1/admin/follow-ups";
    private const int MaxBodyBytes = 128 * 1024;

    public static void MapCustomerInteractions(this IEndpointRouteBuilder endpoints)
    {
        var customerInteractions = endpoints.MapGroup(CustomersRoot)
            .WithTags("CustomerInteractions")
            .RequireAuthorization(AdminSecurity.Ready);

        // 1. GET /api/v1/admin/customers/{id}/interactions (05-api.md:208)
        customerInteractions.MapGet("/{id:guid}/interactions", async (
            Guid id,
            HttpContext http,
            IQueryHandler<ListCustomerInteractions, CatalogPage<CustomerInteractionView>> handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
        {
            var result = await handler.Handle(new ListCustomerInteractions(id, page, pageSize), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.read")
        .Produces<CatalogPage<CustomerInteractionView>>()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        // 2. POST /api/v1/admin/customers/{id}/interactions (05-api.md:209)
        customerInteractions.MapPost("/{id:guid}/interactions", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CreateCustomerInteraction, CustomerInteractionView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<CreateCustomerInteractionRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new CreateCustomerInteraction(
                id,
                body!.Channel!,
                body.Kind!,
                body.Content!,
                body.OccurredAt,
                body.OrderId,
                body.FollowUpAt,
                CustomerEndpoints.Actor(http)), ct);

            return result.IsSuccess
                ? Results.Created($"{CustomersRoot}/{id}/interactions/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CreateCustomerInteractionRequest>()
        .Produces<CustomerInteractionView>(StatusCodes.Status201Created)
        .WriteErrors();

        // 3. PATCH /api/v1/admin/customers/{id}/interactions/{iid} (05-api.md:209)
        customerInteractions.MapPatch("/{id:guid}/interactions/{iid:guid}", async (
            Guid id,
            Guid iid,
            HttpContext http,
            ICommandHandler<PatchCustomerInteraction, CustomerInteractionView> handler,
            CancellationToken ct) =>
        {
            var (fields, errorResult) = await ReadRawObjectAsync(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PatchCustomerInteraction(id, iid, fields, CustomerEndpoints.Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PatchCustomerInteractionRequest>()
        .Produces<CustomerInteractionView>()
        .WriteErrors();

        // 4. POST /api/v1/admin/customers/{id}/interactions/{iid}/complete-follow-up (05-api.md:209)
        customerInteractions.MapPost("/{id:guid}/interactions/{iid:guid}/complete-follow-up", async (
            Guid id,
            Guid iid,
            HttpContext http,
            ICommandHandler<CompleteInteractionFollowUp, CustomerInteractionView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<CompleteFollowUpRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new CompleteInteractionFollowUp(
                id,
                iid,
                body!.Result!,
                CustomerEndpoints.Actor(http)), ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CompleteFollowUpRequest>()
        .Produces<CustomerInteractionView>()
        .WriteErrors();

        // 5. GET /api/v1/admin/follow-ups?due=today|overdue|week (05-api.md:210)
        endpoints.MapGet(FollowUpsRoot, async (
            HttpContext http,
            IQueryHandler<ListFollowUps, CatalogPage<FollowUpItemView>> handler,
            CancellationToken ct,
            string? due = null,
            int page = 1,
            int pageSize = 20) =>
        {
            if (http.Request.Query["due"].Count > 1)
            {
                return new Error("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
                    new Dictionary<string, string[]>(StringComparer.Ordinal) { ["due"] = ["invalid_due_filter"] })
                    .ToProblem(http);
            }

            var result = await handler.Handle(new ListFollowUps(due, page, pageSize), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("CustomerInteractions")
        .RequireAuthorization(AdminSecurity.Ready, "customers.read")
        .Produces<CatalogPage<FollowUpItemView>>()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
    }

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

    private static RouteHandlerBuilder WriteErrors(this RouteHandlerBuilder builder)
        => builder.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(415);

    private static IResult UnsupportedMediaType(HttpContext http)
        => AdminSecurity.Problem(http, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

    private static IResult InvalidBody(HttpContext http)
        => new Error("validation_failed", "Dữ liệu yêu cầu không hợp lệ.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = ["invalid_body"] }).ToProblem(http);

    private static async Task<(byte[]? Bytes, IResult? Error)> ReadBufferAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return (null, InvalidBody(http));
        }

        using var ms = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (ms.Length + read > MaxBodyBytes)
            {
                return (null, InvalidBody(http));
            }

            ms.Write(chunk, 0, read);
        }

        return (ms.ToArray(), null);
    }

    private static async Task<(JsonElement Element, IResult? Error)> ReadRawObjectAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
        {
            return (default, UnsupportedMediaType(http));
        }

        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (default, readError);
        if (bytes!.Length == 0) return (default, InvalidBody(http));

        try
        {
            using var doc = JsonDocument.Parse(bytes, InteractionJson.StrictDocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (default, InvalidBody(http));
            }

            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, InvalidBody(http));
        }
    }

    private static async Task<(T? Value, IResult? Error)> ReadTypedBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            return (null, UnsupportedMediaType(http));
        }

        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (null, readError);
        if (bytes!.Length == 0) return (null, InvalidBody(http));

        try
        {
            using var doc = JsonDocument.Parse(bytes, InteractionJson.StrictDocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, InvalidBody(http));
            }

            var value = JsonSerializer.Deserialize<T>(bytes, InteractionJson.StrictSerializerOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }
}
