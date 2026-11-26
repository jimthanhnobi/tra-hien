using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Crm;

public sealed record CreateCustomerRequest(
    string? FullName = null,
    string? Phone = null,
    string? Email = null,
    string? ZaloName = null,
    string? CustomerGroup = null,
    bool IsVip = false,
    string? AcquisitionSource = null,
    Guid? ReferredByCustomerId = null,
    string? ContactStatus = null,
    string? NoteSummary = null,
    bool HadLegacyPurchase = false,
    Guid[]? TagIds = null);

public sealed class PatchCustomerRequest
{
    public string RowVersion { get; init; } = "";
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? ZaloName { get; init; }
    public string? CustomerGroup { get; init; }
    public bool? IsVip { get; init; }
    public Guid? ReferredByCustomerId { get; init; }
    public string? ContactStatus { get; init; }
    public string? NoteSummary { get; init; }
}

public sealed class CorrectCustomerIdentityRequest
{
    public string RowVersion { get; init; } = "";
    public string? Phone { get; init; }
    public string? Origin { get; init; }
    public string? AcquisitionSource { get; init; }
    public bool? HadLegacyPurchase { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record CreateTagRequest(string? Name = null, string? Color = null);

public sealed class PatchTagRequest
{
    public string? Name { get; init; }
    public string? Color { get; init; }
}

public sealed record PutCustomerTagsRequest(Guid[]? TagIds = null, string? RowVersion = null);

public sealed record CreateCustomerAddressRequest(
    string? Label = null,
    string? ReceiverName = null,
    string? ReceiverPhone = null,
    string? ProvinceCode = null,
    string? WardCode = null,
    string? WardNameText = null,
    string? AddressLine = null,
    bool IsDefault = false,
    string? RowVersion = null);

public sealed class PatchCustomerAddressRequest
{
    public string? Label { get; init; }
    public string? ReceiverName { get; init; }
    public string? ReceiverPhone { get; init; }
    public string? ProvinceCode { get; init; }
    public string? WardCode { get; init; }
    public string? WardNameText { get; init; }
    public string? AddressLine { get; init; }
    public bool? IsDefault { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record DeleteCustomerAddressRequest(string? RowVersion = null);

public static class CustomerEndpoints
{
    private const string CustomersRoot = "/api/v1/admin/customers";
    private const string TagsRoot = "/api/v1/admin/tags";
    private const int MaxBodyBytes = 128 * 1024;

    private static readonly JsonDocumentOptions StrictDocOptions = new()
    {
        AllowDuplicateProperties = false
    };

    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    private static readonly HashSet<string> OwnerOnlyIdentityFields = new(StringComparer.Ordinal)
    {
        "origin", "acquisitionSource", "hadLegacyPurchase"
    };

    public static void MapCustomers(this IEndpointRouteBuilder endpoints)
    {
        var customers = endpoints.MapGroup(CustomersRoot)
            .WithTags("Customers")
            .RequireAuthorization(AdminSecurity.Ready);

        // P2-03: POST /api/v1/admin/customers/import?dryRun=true|false
        customers.MapCustomerImport();

        // 1. GET /api/v1/admin/customers
        customers.MapGet("", async (
            HttpContext http,
            IQueryHandler<ListCustomers, CatalogPage<CustomerSummaryView>> handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20,
            string? sort = null,
            string? q = null,
            string? group = null,
            Guid? tag = null,
            string? interest = null,
            string? lastDeliveredBefore = null,
            string? lastDeliveredAfter = null,
            long? minLtv = null,
            string? contactStatus = null,
            string? hasConsent = null) =>
        {
            var result = await handler.Handle(new ListCustomers(
                page, pageSize, sort, q, group, tag, interest,
                lastDeliveredBefore, lastDeliveredAfter, minLtv, contactStatus, hasConsent), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.read")
        .Produces<CatalogPage<CustomerSummaryView>>()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        // 2. POST /api/v1/admin/customers
        customers.MapPost("", async (
            HttpContext http,
            ICommandHandler<CreateCustomer, CustomerDetailView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<CreateCustomerRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new CreateCustomer(
                body!.FullName!,
                body.Phone,
                body.Email,
                body.ZaloName,
                body.CustomerGroup,
                body.IsVip,
                body.AcquisitionSource,
                body.ReferredByCustomerId,
                body.ContactStatus,
                body.NoteSummary,
                body.HadLegacyPurchase,
                body.TagIds,
                Actor(http)), ct);

            return result.IsSuccess
                ? Results.Created($"{CustomersRoot}/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CreateCustomerRequest>()
        .Produces<CustomerDetailView>(StatusCodes.Status201Created)
        .WriteErrors();

        // 3. GET /api/v1/admin/customers/{id}
        customers.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            IQueryHandler<GetCustomer, CustomerDetailView> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetCustomer(id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.read")
        .Produces<CustomerDetailView>()
        .ReadErrors();

        // 4. PATCH /api/v1/admin/customers/{id}
        customers.MapPatch("/{id:guid}", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PatchCustomer, CustomerDetailView> handler,
            CancellationToken ct) =>
        {
            var (fields, errorResult) = await ReadRawObjectAsync(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PatchCustomer(id, fields, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PatchCustomerRequest>()
        .Produces<CustomerDetailView>()
        .WriteErrors();

        // 5. POST /api/v1/admin/customers/{id}/correct-identity
        customers.MapPost("/{id:guid}/correct-identity", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CorrectCustomerIdentity, CustomerDetailView> handler,
            CancellationToken ct) =>
        {
            var (fields, errorResult) = await ReadRawObjectAsync(http, ct);
            if (errorResult is not null) return errorResult;

            var actor = Actor(http);
            if (!actor.IsOwner && fields.EnumerateObject().Any(p => OwnerOnlyIdentityFields.Contains(p.Name)))
            {
                return new Error("forbidden", "Chỉ chủ cửa hàng mới có quyền thay đổi trường nguồn gốc khách hàng.", ErrorType.Forbidden)
                    .ToProblem(http);
            }

            var result = await handler.Handle(new CorrectCustomerIdentity(id, fields, actor), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CorrectCustomerIdentityRequest>()
        .Produces<CustomerDetailView>()
        .WriteErrors();

        // 6. GET /api/v1/admin/customers/{id}/orders (GAP-P202-03 = A)
        customers.MapGet("/{id:guid}/orders", async (
            Guid id,
            HttpContext http,
            IQueryHandler<ListCustomerOrders, CatalogPage<CustomerOrderSummaryView>> handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
        {
            var result = await handler.Handle(new ListCustomerOrders(id, page, pageSize), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.read")
        .Produces<CatalogPage<CustomerOrderSummaryView>>()
        .ProducesProblem(400).ReadErrors();

        // 10. PUT /api/v1/admin/customers/{id}/tags
        customers.MapPut("/{id:guid}/tags", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PutCustomerTags, CustomerTagsResult> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<PutCustomerTagsRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PutCustomerTags(id, body!.TagIds!, body.RowVersion), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PutCustomerTagsRequest>()
        .Produces<CustomerTagsResult>()
        .WriteErrors();

        // 11. GET /api/v1/admin/customers/{id}/addresses (GAP-P202-02 = B: customers.write)
        customers.MapGet("/{id:guid}/addresses", async (
            Guid id,
            HttpContext http,
            IQueryHandler<ListCustomerAddresses, IReadOnlyList<CustomerAddressView>> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new ListCustomerAddresses(id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .Produces<IReadOnlyList<CustomerAddressView>>()
        .ReadErrors();

        // 12. GET /api/v1/admin/customers/{id}/addresses/{aid} (GAP-P202-02 = B: customers.write)
        customers.MapGet("/{id:guid}/addresses/{aid:guid}", async (
            Guid id,
            Guid aid,
            HttpContext http,
            IQueryHandler<GetCustomerAddress, CustomerAddressView> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetCustomerAddress(id, aid), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .Produces<CustomerAddressView>()
        .ReadErrors();

        // 13. POST /api/v1/admin/customers/{id}/addresses
        customers.MapPost("/{id:guid}/addresses", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CreateCustomerAddress, CustomerAddressView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<CreateCustomerAddressRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new CreateCustomerAddress(
                id,
                body!.Label,
                body.ReceiverName!,
                body.ReceiverPhone!,
                body.ProvinceCode!,
                body.WardCode,
                body.WardNameText,
                body.AddressLine!,
                body.IsDefault,
                body.RowVersion), ct);

            return result.IsSuccess
                ? Results.Created($"{CustomersRoot}/{id}/addresses/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CreateCustomerAddressRequest>()
        .Produces<CustomerAddressView>(StatusCodes.Status201Created)
        .WriteErrors()
        .ProducesProblem(422);

        // 14. PATCH /api/v1/admin/customers/{id}/addresses/{aid}
        customers.MapPatch("/{id:guid}/addresses/{aid:guid}", async (
            Guid id,
            Guid aid,
            HttpContext http,
            ICommandHandler<PatchCustomerAddress, CustomerAddressView> handler,
            CancellationToken ct) =>
        {
            var (fields, errorResult) = await ReadRawObjectAsync(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PatchCustomerAddress(id, aid, fields), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PatchCustomerAddressRequest>()
        .Produces<CustomerAddressView>()
        .WriteErrors()
        .ProducesProblem(422);

        // 15. DELETE /api/v1/admin/customers/{id}/addresses/{aid}
        customers.MapDelete("/{id:guid}/addresses/{aid:guid}", async (
            Guid id,
            Guid aid,
            HttpContext http,
            ICommandHandler<DeleteCustomerAddress, bool> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadOptionalTypedBodyAsync<DeleteCustomerAddressRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new DeleteCustomerAddress(id, aid, body?.RowVersion), ct);
            return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem(http);
        })
        .RequireAuthorization("customers.write")
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<DeleteCustomerAddressRequest>(required: false)
        .Produces(StatusCodes.Status204NoContent)
        .WriteErrors();

        // Tags group: /api/v1/admin/tags (GAP-P202-02 = B: customers.write on GET/POST/PATCH, route PATCH /{id})
        var tags = endpoints.MapGroup(TagsRoot)
            .WithTags("CustomerTags")
            .RequireAuthorization(AdminSecurity.Ready, "customers.write");

        // 7. GET /api/v1/admin/tags
        tags.MapGet("", async (
            HttpContext http,
            IQueryHandler<ListTags, IReadOnlyList<CustomerTagView>> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new ListTags(), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .Produces<IReadOnlyList<CustomerTagView>>()
        .ProducesProblem(401).ProducesProblem(403);

        // 8. POST /api/v1/admin/tags
        tags.MapPost("", async (
            HttpContext http,
            ICommandHandler<CreateTag, CustomerTagView> handler,
            CancellationToken ct) =>
        {
            var (body, errorResult) = await ReadTypedBodyAsync<CreateTagRequest>(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new CreateTag(body!.Name!, body.Color), ct);
            return result.IsSuccess
                ? Results.Created($"{TagsRoot}/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<CreateTagRequest>()
        .Produces<CustomerTagView>(StatusCodes.Status201Created)
        .WriteErrors();

        // 9. PATCH /api/v1/admin/tags/{id}
        tags.MapPatch("/{id:guid}", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PatchTag, CustomerTagView> handler,
            CancellationToken ct) =>
        {
            var (fields, errorResult) = await ReadRawObjectAsync(http, ct);
            if (errorResult is not null) return errorResult;

            var result = await handler.Handle(new PatchTag(id, fields), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithMetadata(new RequireCsrfHeader())
        .WithJsonRequest<PatchTagRequest>()
        .Produces<CustomerTagView>()
        .WriteErrors();
    }

    // Document manually bound JSON bodies without .Accepts metadata, which short-circuits routing with an empty 415.
    private static RouteHandlerBuilder WithJsonRequest<T>(this RouteHandlerBuilder builder, bool required = true) =>
        builder.AddOpenApiOperationTransformer(async (operation, context, ct) =>
        {
            var document = context.Document!;
            var schemaName = typeof(T).Name;
            document.AddComponent(schemaName, await context.GetOrCreateSchemaAsync(typeof(T), cancellationToken: ct));
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = required,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchemaReference(schemaName, document) }
                }
            };
        });

    internal static CustomerActor Actor(HttpContext http) => new(
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        http.User.FindAll("permission").Select(c => c.Value).ToArray(),
        http.User.IsInRole("owner"));

    private static RouteHandlerBuilder ReadErrors(this RouteHandlerBuilder builder)
        => builder.ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

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
            using var doc = JsonDocument.Parse(bytes, StrictDocOptions);
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
            using var doc = JsonDocument.Parse(bytes, StrictDocOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, InvalidBody(http));
            }

            var value = JsonSerializer.Deserialize<T>(bytes, StrictJsonOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }

    private static async Task<(T? Value, IResult? Error)> ReadOptionalTypedBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (null, readError);
        if (bytes!.Length == 0) return (null, null);

        if (!http.Request.HasJsonContentType())
        {
            return (null, UnsupportedMediaType(http));
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes, StrictDocOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, InvalidBody(http));
            }

            var value = JsonSerializer.Deserialize<T>(bytes, StrictJsonOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }
}
