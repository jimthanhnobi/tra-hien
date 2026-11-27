using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Crm;

public sealed record PutCustomerConsentRequest(
    string? Channel = null,
    string? Purpose = null,
    string? Status = null,
    string? Evidence = null,
    string? RowVersion = null);

public sealed record MergeCustomersRequest(
    Guid? MergeCustomerId = null,
    string? RowVersion = null,
    string? MergeRowVersion = null);

public sealed record EraseCustomerRequest(
    string? Reason = null,
    string? RequestChannel = null,
    string? RowVersion = null);

public static class CustomerPrivacyEndpoints
{
    private const string CustomersRoot = "/api/v1/admin/customers";
    private const int MaxBodyBytes = 128 * 1024;

    public static void MapCustomerPrivacy(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CustomersRoot)
            .WithTags("CustomerPrivacy")
            .RequireAuthorization(AdminSecurity.Ready);

        // 1. PUT /api/v1/admin/customers/{id}/consents (05-api.md §4.5, BR-CRM-09)
        group.MapPut("/{id:guid}/consents", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PutCustomerConsent, CustomerConsentsResult> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<PutCustomerConsentRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PutCustomerConsent(
                id,
                body!.Channel!,
                body.Purpose!,
                body.Status!,
                body.Evidence!,
                body.RowVersion,
                CustomerEndpoints.Actor(http)), ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PutCustomerConsentRequest>()
        .Produces<CustomerConsentsResult>()
        .WriteErrors();

        // 2. POST /api/v1/admin/customers/{id}/merge (05-api.md §4.5, BR-CRM-05)
        group.MapPost("/{id:guid}/merge", async (
            Guid id,
            HttpContext http,
            ICommandHandler<MergeCustomers, CustomerDetailView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<MergeCustomersRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new MergeCustomers(
                id,
                body!.MergeCustomerId ?? Guid.Empty,
                body.RowVersion,
                body.MergeRowVersion,
                CustomerEndpoints.Actor(http)), ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.merge")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<MergeCustomersRequest>()
        .Produces<CustomerDetailView>()
        .WriteErrors();

        // 3. POST /api/v1/admin/customers/{id}/erase (05-api.md §4.5, BR-CRM-11, AC-P2-10)
        group.MapPost("/{id:guid}/erase", async (
            Guid id,
            HttpContext http,
            ICommandHandler<EraseCustomer, CustomerEraseResult> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<EraseCustomerRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new EraseCustomer(
                id,
                body!.Reason!,
                body.RequestChannel!,
                body.RowVersion,
                CustomerEndpoints.Actor(http)), ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.erase")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<EraseCustomerRequest>()
        .Produces<CustomerEraseResult>()
        .WriteErrors();

        // 4. GET /api/v1/admin/customers/{id}/export (05-api.md §4.5, BR-CRM-13, NFR-12)
        group.MapGet("/{id:guid}/export", async (
            Guid id,
            HttpContext http,
            ICommandHandler<ExportCustomerData, CustomerDataExportView> handler,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var result = await handler.Handle(new ExportCustomerData(id, CustomerEndpoints.Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.export")
        .Produces<CustomerDataExportView>()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
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

    private static async Task<(T? Value, IResult? Error)> ReadTypedBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            return (null, UnsupportedMediaType(http));
        }

        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (null, readError);
        if (bytes!.Length == 0) return (null, InvalidBody(http));

        return CustomerPrivacyJson.TryDeserializeObject<T>(bytes, out var value)
            ? (value, null)
            : (null, InvalidBody(http));
    }
}
