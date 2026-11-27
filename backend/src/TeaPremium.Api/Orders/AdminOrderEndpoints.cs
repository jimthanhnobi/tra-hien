using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Idempotency;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Api.Orders;

public sealed record CreateAdminOrderNewCustomerRequest(
    string FullName,
    string Phone);

public sealed record CreateAdminOrderLineRequest(
    Guid VariantId,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Quantity,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? UnitPriceVnd = null,
    string? PriceOverrideReason = null);

public sealed record CreateAdminOrderShippingRequest(
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string AddressLine,
    string? WardCode = null,
    string? WardNameText = null,
    string? CustomerNote = null);

public sealed record CreateAdminOrderRequest(
    string SalesChannel,
    string PaymentMethod,
    CreateAdminOrderLineRequest[] Lines,
    CreateAdminOrderShippingRequest Shipping,
    Guid? CustomerId = null,
    CreateAdminOrderNewCustomerRequest? NewCustomer = null,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? DiscountVnd = null,
    string? DiscountNote = null,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? ShippingFeeVnd = null,
    string? InternalNote = null,
    bool ConfirmNow = false,
    bool CallConfirmed = false,
    string? CallNote = null,
    bool IsBackfilled = false,
    DateTimeOffset? DeliveredAt = null);

public sealed class PatchAdminOrderShippingRequest
{
    public string? ReceiverName { get; init; }
    public string? ReceiverPhone { get; init; }
    public string? ProvinceCode { get; init; }
    public string? WardCode { get; init; }
    public string? WardNameText { get; init; }
    public string? AddressLine { get; init; }
    public string? CustomerNote { get; init; }
}

public sealed class PatchAdminOrderRequest
{
    public string RowVersion { get; init; } = "";
    public CreateAdminOrderLineRequest[]? Lines { get; init; }
    public PatchAdminOrderShippingRequest? Shipping { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long? DiscountVnd { get; init; }
    public string? DiscountNote { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long? ShippingFeeVnd { get; init; }
    public string? InternalNote { get; init; }
}

public sealed record ConfirmAdminOrderRequest(
    string RowVersion,
    bool CallConfirmed = false,
    string? CallNote = null,
    string? Note = null);

public sealed record CancelAdminOrderRequest(
    string RowVersion,
    string Reason);

public sealed record ReorderAdminOrderRequest(
    string? SalesChannel = null,
    string? PaymentMethod = null,
    string? InternalNote = null);

public static class AdminOrderEndpoints
{
    private const string OrdersRoot = "/api/v1/admin/orders";
    private const int MaxIdempotentBodyBytes = 64 * 1024;
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
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapAdminOrders(this IEndpointRouteBuilder endpoints)
    {
        // 1. GET /api/v1/admin/orders (05-api.md:179)
        endpoints.MapGet(OrdersRoot, async (
            HttpContext http,
            IQueryHandler<ListAdminOrders, CatalogPage<AdminOrderListItemView>> handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20,
            string? sort = null,
            string? status = null,
            string? paymentStatus = null,
            string? channel = null,
            string? origin = null,
            string? from = null,
            string? to = null,
            string? q = null,
            Guid? customerId = null,
            string? flag = null) =>
        {
            var result = await handler.Handle(new ListAdminOrders(
                page, pageSize, sort, status, paymentStatus, channel, origin, from, to, q, customerId, flag), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.read")
        .Produces<CatalogPage<AdminOrderListItemView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 2. GET /api/v1/admin/orders/{id} (05-api.md:180)
        endpoints.MapGet($"{OrdersRoot}/{{id:guid}}", async (
            Guid id,
            HttpContext http,
            IQueryHandler<GetAdminOrderDetail, AdminOrderDetailView> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new GetAdminOrderDetail(id, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.read")
        .Produces<AdminOrderDetailView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // 3. POST /api/v1/admin/orders [idem] (05-api.md:181, G11=A, BR-PRC-06/07, BR-ORD-09, ASSUMPTION-P206-04=A)
        endpoints.MapPost(OrdersRoot, async (
            HttpContext http,
            ICommandHandler<CreateAdminOrderHttp, AdminOrderCreatedView> handler,
            CancellationToken ct) =>
        {
            var (body, parseError) = await ReadStrictJsonAsync<CreateAdminOrderRequest>(
                http, MaxIdempotentBodyBytes, optionalOffsetDateProperty: "deliveredAt", ct);
            if (parseError is not null) return (object)parseError;

            if (body is null
                || body.Lines is null
                || body.Lines.Any(l => l is null)
                || body.Shipping is null
                || body.SalesChannel is null
                || body.PaymentMethod is null
                || body.Shipping.ReceiverName is null
                || body.Shipping.ReceiverPhone is null
                || body.Shipping.ProvinceCode is null
                || body.Shipping.AddressLine is null
                || (body.NewCustomer is not null && (body.NewCustomer.FullName is null || body.NewCustomer.Phone is null)))
                return (object)InvalidBody(http);

            var actor = Actor(http);
            if (body.IsBackfilled && !actor.IsOwner)
                return (object)new Error("forbidden", "Chỉ chủ cửa hàng mới được tạo đơn backfill lịch sử.", ErrorType.Forbidden).ToProblem(http);
            if (body.Lines.Any(l => l.UnitPriceVnd is not null) && !actor.HasOverridePrice)
                return (object)new Error("forbidden", "Bạn không có quyền sửa đơn giá dòng hàng.", ErrorType.Forbidden).ToProblem(http);

            var result = await handler.Handle(new CreateAdminOrderHttp(
                body.CustomerId,
                body.NewCustomer is null ? null : new AdminOrderNewCustomerInput(body.NewCustomer.FullName, body.NewCustomer.Phone),
                body.SalesChannel,
                body.PaymentMethod,
                body.Lines.Select(l => new AdminOrderLineInput(l.VariantId, l.Quantity, l.UnitPriceVnd, l.PriceOverrideReason)).ToArray(),
                body.DiscountVnd,
                body.DiscountNote,
                body.ShippingFeeVnd,
                new AdminOrderShippingInput(
                    body.Shipping.ReceiverName,
                    body.Shipping.ReceiverPhone,
                    body.Shipping.ProvinceCode,
                    body.Shipping.WardCode,
                    body.Shipping.WardNameText,
                    body.Shipping.AddressLine,
                    body.Shipping.CustomerNote),
                body.InternalNote,
                body.ConfirmNow,
                body.CallConfirmed,
                body.CallNote,
                body.IsBackfilled,
                body.DeliveredAt,
                actor), ct);

            return result.IsSuccess
                ? (object)new IdempotentResponse(StatusCodes.Status201Created, result.Value)
                : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<CreateAdminOrderRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<AdminOrderCreatedView>(StatusCodes.Status201Created)
        .WriteErrors()
        .RequireJsonContentType()
        .WithIdempotency(new(
            "orders.admin.create.v1",
            http => AdminSecurity.Scheme + ":" + ActorId(http).ToString("D"),
            SafeCreateOrderResponse))
        .AddIdempotencyKeyHeaderParameter();

        // 4. PATCH /api/v1/admin/orders/{id} (05-api.md:182, BR-ORD-08, ASSUMPTION-P206-01=A, G12=A, AC-P2-05/06, BR-PRC-07)
        endpoints.MapPatch($"{OrdersRoot}/{{id:guid}}", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PatchAdminOrder, AdminOrderDetailView> handler,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var (command, parseError) = await ReadPatchAdminOrderAsync(id, actor, http, ct);
            if (parseError is not null) return parseError;

            if (command!.HasLines && command.Lines!.Any(l => l.UnitPriceVnd is not null) && !actor.HasOverridePrice)
                return new Error("forbidden", "Bạn không có quyền sửa đơn giá dòng hàng.", ErrorType.Forbidden).ToProblem(http);

            var result = await handler.Handle(command, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<PatchAdminOrderRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<AdminOrderDetailView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();

        // 5. POST /api/v1/admin/orders/{id}/confirm (05-api.md:183, G11=A)
        endpoints.MapPost($"{OrdersRoot}/{{id:guid}}/confirm", async (
            Guid id,
            HttpContext http,
            ICommandHandler<ConfirmAdminOrder, AdminOrderDetailView> handler,
            CancellationToken ct) =>
        {
            var (body, parseError) = await ReadStrictJsonAsync<ConfirmAdminOrderRequest>(http, MaxBodyBytes, optionalOffsetDateProperty: null, ct);
            if (parseError is not null) return parseError;

            if (!CatalogVersions.TryDecode(body!.RowVersion, out var expectedVersion))
                return InvalidRowVersion(http);

            var result = await handler.Handle(new ConfirmAdminOrder(
                id, expectedVersion, body.CallConfirmed, body.CallNote, body.Note, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<ConfirmAdminOrderRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<AdminOrderDetailView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();

        // 6. POST /api/v1/admin/orders/{id}/cancel (05-api.md:184)
        endpoints.MapPost($"{OrdersRoot}/{{id:guid}}/cancel", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CancelAdminOrder, AdminOrderDetailView> handler,
            CancellationToken ct) =>
        {
            var (body, parseError) = await ReadStrictJsonAsync<CancelAdminOrderRequest>(http, MaxBodyBytes, optionalOffsetDateProperty: null, ct);
            if (parseError is not null) return parseError;

            if (body is null || !CatalogVersions.TryDecode(body.RowVersion, out var expectedVersion))
                return InvalidRowVersion(http);

            if (body.Reason is null)
                return InvalidBody(http);

            var result = await handler.Handle(new CancelAdminOrder(id, expectedVersion, body.Reason, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<CancelAdminOrderRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<AdminOrderDetailView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();

        // 7. POST /api/v1/admin/orders/{id}/reorder (05-api.md:189, BR-ORD-03, ASSUMPTION-P206-03=A)
        endpoints.MapPost($"{OrdersRoot}/{{id:guid}}/reorder", async (
            Guid id,
            HttpContext http,
            ICommandHandler<ReorderAdminOrder, AdminOrderDetailView> handler,
            CancellationToken ct) =>
        {
            var (body, parseError) = await ReadOptionalJsonAsync<ReorderAdminOrderRequest>(http, MaxBodyBytes, ct);
            if (parseError is not null) return parseError;

            var result = await handler.Handle(new ReorderAdminOrder(
                id, body?.SalesChannel, body?.PaymentMethod, body?.InternalNote, Actor(http)), ct);

            return result.IsSuccess
                ? Results.Created($"{OrdersRoot}/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<ReorderAdminOrderRequest>(required: false)
        .WithMetadata(new RequireCsrfHeader())
        .Produces<AdminOrderDetailView>(StatusCodes.Status201Created)
        .WriteErrors();

        // 8. GET /api/v1/admin/orders/{id}/packing-slip (05-api.md:190, BR-ORD-12)
        endpoints.MapGet($"{OrdersRoot}/{{id:guid}}/packing-slip", async (
            Guid id,
            HttpContext http,
            IQueryHandler<GetAdminOrderPackingSlip, string> handler,
            CancellationToken ct,
            string? hidePrices = null) =>
        {
            var rawValues = http.Request.Query["hidePrices"];
            var hide = false;
            if (rawValues.Count > 1)
                return new Error("validation_failed", "Tham số hidePrices không hợp lệ.", ErrorType.Validation).ToProblem(http);
            if (rawValues.Count == 1 && !string.IsNullOrEmpty(rawValues[0]))
            {
                if (!bool.TryParse(rawValues[0], out hide))
                    return new Error("validation_failed", "Tham số hidePrices phải là true hoặc false.", ErrorType.Validation).ToProblem(http);
            }

            var result = await handler.Handle(new GetAdminOrderPackingSlip(id, hide), ct);
            if (!result.IsSuccess) return result.Error.ToProblem(http);

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            http.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
            return Results.Content(result.Value, "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status200OK);
        })
        .WithTags("Admin orders")
        .RequireAuthorization(AdminSecurity.Ready, "orders.read")
        .Produces<string>(StatusCodes.Status200OK, "text/html")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ASSUMPTION-P206-04 = A: delegates to shared allowlist policy in Application.
    internal static bool SafeCreateOrderResponse(JsonElement value) =>
        AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(value);

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

    private static RouteHandlerBuilder RequireJsonContentType(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.HasJsonContentType())
                return UnsupportedMediaType(http);
            return await next(context);
        });

    private static RouteHandlerBuilder WriteErrors(this RouteHandlerBuilder route) =>
        route.ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    private static RouteHandlerBuilder AddIdempotencyKeyHeaderParameter(this RouteHandlerBuilder route) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
            });
            return Task.CompletedTask;
        });

    private static Guid ActorId(HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static OrderActor Actor(HttpContext http) => new(
        ActorId(http),
        http.User.IsInRole("owner"),
        http.User.HasClaim("permission", "costs.read"),
        http.User.HasClaim("permission", "orders.override_price"));

    private static IResult UnsupportedMediaType(HttpContext http) =>
        AdminSecurity.Problem(http, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

    private static IResult InvalidBody(HttpContext http) =>
        new Error("validation_failed", "Nội dung yêu cầu không hợp lệ.", ErrorType.Validation).ToProblem(http);

    private static IResult InvalidRowVersion(HttpContext http) =>
        new Error("validation_failed", "Phiên bản dữ liệu (rowVersion) không hợp lệ.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["rowVersion"] = ["invalid_row_version"] }).ToProblem(http);

    private static async Task<(byte[]? Bytes, IResult? Error)> ReadBufferAsync(HttpContext http, int maxBytes, bool allowEmpty, CancellationToken ct)
    {
        if (http.Request.ContentLength > maxBytes)
            return (null, InvalidBody(http));

        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (int read; (read = await http.Request.Body.ReadAsync(buffer, ct)) > 0;)
        {
            if (data.Length + read > maxBytes)
                return (null, InvalidBody(http));
            data.Write(buffer, 0, read);
        }

        if (data.Length == 0)
            return allowEmpty ? ([], null) : (null, InvalidBody(http));

        if (!http.Request.HasJsonContentType())
            return (null, UnsupportedMediaType(http));

        return (data.ToArray(), null);
    }

    private static async Task<(T? Value, IResult? Error)> ReadStrictJsonAsync<T>(
        HttpContext http,
        int maxBytes,
        string? optionalOffsetDateProperty,
        CancellationToken ct)
        where T : class
    {
        if (!http.Request.HasJsonContentType())
            return (null, UnsupportedMediaType(http));

        var (bytes, readError) = await ReadBufferAsync(http, maxBytes, allowEmpty: false, ct);
        if (readError is not null) return (null, readError);

        try
        {
            using var doc = JsonDocument.Parse(bytes!, StrictDocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, InvalidBody(http));

            if (optionalOffsetDateProperty is not null
                && root.TryGetProperty(optionalOffsetDateProperty, out var dateEl)
                && dateEl.ValueKind != JsonValueKind.Null)
            {
                if (dateEl.ValueKind != JsonValueKind.String || !HasExplicitIsoOffset(dateEl.GetString()))
                    return (null, InvalidBody(http));
            }

            var value = root.Deserialize<T>(StrictJsonOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }

    private static async Task<(T? Value, IResult? Error)> ReadOptionalJsonAsync<T>(
        HttpContext http,
        int maxBytes,
        CancellationToken ct)
        where T : class
    {
        var (bytes, readError) = await ReadBufferAsync(http, maxBytes, allowEmpty: true, ct);
        if (readError is not null) return (null, readError);
        if (bytes!.Length == 0) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(bytes, StrictDocOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, InvalidBody(http));

            var value = doc.RootElement.Deserialize<T>(StrictJsonOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }

    // ASSUMPTION-P206-01 = A: raw JsonDocument parser preserving exact 3-way distinction for shippingFeeVnd (omitted vs null vs value)
    // and explicit presence flags for all partial PATCH fields.
    private static async Task<(PatchAdminOrder? Command, IResult? Error)> ReadPatchAdminOrderAsync(
        Guid orderId,
        OrderActor actor,
        HttpContext http,
        CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return (null, UnsupportedMediaType(http));

        var (bytes, readError) = await ReadBufferAsync(http, MaxBodyBytes, allowEmpty: false, ct);
        if (readError is not null) return (null, readError);

        try
        {
            using var doc = JsonDocument.Parse(bytes!, StrictDocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, InvalidBody(http));

            var hasRowVersion = false;
            string? rowVersionRaw = null;
            var hasLines = false;
            List<AdminOrderLineInput>? lines = null;
            var hasShipping = false;
            PatchAdminOrderShippingInput? shipping = null;
            var hasDiscountVnd = false;
            long? discountVnd = null;
            var hasDiscountNote = false;
            string? discountNote = null;
            var hasShippingFeeVnd = false;
            long? shippingFeeVnd = null;
            var hasInternalNote = false;
            string? internalNote = null;

            foreach (var prop in root.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "rowVersion":
                        hasRowVersion = true;
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            return (null, InvalidRowVersion(http));
                        rowVersionRaw = prop.Value.GetString();
                        break;

                    case "lines":
                        hasLines = true;
                        if (prop.Value.ValueKind != JsonValueKind.Array)
                            return (null, InvalidBody(http));
                        var parsedLines = TryParseLinesArray(prop.Value);
                        if (parsedLines is null)
                            return (null, InvalidBody(http));
                        lines = parsedLines;
                        break;

                    case "shipping":
                        hasShipping = true;
                        if (prop.Value.ValueKind != JsonValueKind.Object)
                            return (null, InvalidBody(http));
                        var parsedShipping = TryParseShippingPatch(prop.Value);
                        if (parsedShipping is null)
                            return (null, InvalidBody(http));
                        shipping = parsedShipping;
                        break;

                    case "discountVnd":
                        hasDiscountVnd = true;
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt64(out var parsedDiscount) || parsedDiscount < 0)
                            return (null, InvalidBody(http));
                        discountVnd = parsedDiscount;
                        break;

                    case "discountNote":
                        hasDiscountNote = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            discountNote = null;
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                            discountNote = prop.Value.GetString();
                        else
                            return (null, InvalidBody(http));
                        break;

                    case "shippingFeeVnd":
                        hasShippingFeeVnd = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            shippingFeeVnd = null;
                        else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var parsedFee) && parsedFee >= 0)
                            shippingFeeVnd = parsedFee;
                        else
                            return (null, InvalidBody(http));
                        break;

                    case "internalNote":
                        hasInternalNote = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            internalNote = null;
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                            internalNote = prop.Value.GetString();
                        else
                            return (null, InvalidBody(http));
                        break;

                    default:
                        return (null, InvalidBody(http));
                }
            }

            if (!hasRowVersion || !CatalogVersions.TryDecode(rowVersionRaw, out var expectedVersion))
                return (null, InvalidRowVersion(http));

            if (!hasLines && !hasShipping && !hasDiscountVnd && !hasDiscountNote && !hasShippingFeeVnd && !hasInternalNote)
                return (null, InvalidBody(http));

            return (new PatchAdminOrder(
                orderId,
                expectedVersion,
                hasLines,
                lines,
                hasShipping,
                shipping,
                hasDiscountVnd,
                discountVnd,
                hasDiscountNote,
                discountNote,
                hasShippingFeeVnd,
                shippingFeeVnd,
                hasInternalNote,
                internalNote,
                actor), null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }

    private static List<AdminOrderLineInput>? TryParseLinesArray(JsonElement array)
    {
        var result = new List<AdminOrderLineInput>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;

            var hasVariantId = false;
            Guid variantId = Guid.Empty;
            var hasQuantity = false;
            int quantity = 0;
            long? unitPriceVnd = null;
            string? priceOverrideReason = null;

            foreach (var prop in item.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "variantId":
                        if (prop.Value.ValueKind != JsonValueKind.String || !prop.Value.TryGetGuid(out variantId))
                            return null;
                        hasVariantId = true;
                        break;

                    case "quantity":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out quantity))
                            return null;
                        hasQuantity = true;
                        break;

                    case "unitPriceVnd":
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            unitPriceVnd = null;
                        else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var parsedPrice))
                            unitPriceVnd = parsedPrice;
                        else
                            return null;
                        break;

                    case "priceOverrideReason":
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            priceOverrideReason = null;
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                            priceOverrideReason = prop.Value.GetString();
                        else
                            return null;
                        break;

                    default:
                        return null;
                }
            }

            if (!hasVariantId || !hasQuantity) return null;
            result.Add(new AdminOrderLineInput(variantId, quantity, unitPriceVnd, priceOverrideReason));
        }

        return result.Count == 0 ? null : result;
    }

    private static PatchAdminOrderShippingInput? TryParseShippingPatch(JsonElement obj)
    {
        var hasReceiverName = false;
        string? receiverName = null;
        var hasReceiverPhone = false;
        string? receiverPhone = null;
        var hasProvinceCode = false;
        string? provinceCode = null;
        var hasWardCode = false;
        string? wardCode = null;
        var hasWardNameText = false;
        string? wardNameText = null;
        var hasAddressLine = false;
        string? addressLine = null;
        var hasCustomerNote = false;
        string? customerNote = null;

        foreach (var prop in obj.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "receiverName":
                    if (prop.Value.ValueKind != JsonValueKind.String) return null;
                    hasReceiverName = true;
                    receiverName = prop.Value.GetString();
                    break;

                case "receiverPhone":
                    if (prop.Value.ValueKind != JsonValueKind.String) return null;
                    hasReceiverPhone = true;
                    receiverPhone = prop.Value.GetString();
                    break;

                case "provinceCode":
                    if (prop.Value.ValueKind != JsonValueKind.String) return null;
                    hasProvinceCode = true;
                    provinceCode = prop.Value.GetString();
                    break;

                case "wardCode":
                    hasWardCode = true;
                    if (prop.Value.ValueKind == JsonValueKind.Null) wardCode = null;
                    else if (prop.Value.ValueKind == JsonValueKind.String) wardCode = prop.Value.GetString();
                    else return null;
                    break;

                case "wardNameText":
                    hasWardNameText = true;
                    if (prop.Value.ValueKind == JsonValueKind.Null) wardNameText = null;
                    else if (prop.Value.ValueKind == JsonValueKind.String) wardNameText = prop.Value.GetString();
                    else return null;
                    break;

                case "addressLine":
                    if (prop.Value.ValueKind != JsonValueKind.String) return null;
                    hasAddressLine = true;
                    addressLine = prop.Value.GetString();
                    break;

                case "customerNote":
                    hasCustomerNote = true;
                    if (prop.Value.ValueKind == JsonValueKind.Null) customerNote = null;
                    else if (prop.Value.ValueKind == JsonValueKind.String) customerNote = prop.Value.GetString();
                    else return null;
                    break;

                default:
                    return null;
            }
        }

        if (!hasReceiverName && !hasReceiverPhone && !hasProvinceCode && !hasWardCode
            && !hasWardNameText && !hasAddressLine && !hasCustomerNote)
            return null;

        return new PatchAdminOrderShippingInput(
            hasReceiverName, receiverName,
            hasReceiverPhone, receiverPhone,
            hasProvinceCode, provinceCode,
            hasWardCode, wardCode,
            hasWardNameText, wardNameText,
            hasAddressLine, addressLine,
            hasCustomerNote, customerNote);
    }

    private static bool HasExplicitIsoOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.EndsWith('Z') || raw.EndsWith('z')) return true;
        return raw.Length >= 6
            && raw[^6] is '+' or '-'
            && raw[^3] == ':'
            && char.IsAsciiDigit(raw[^5])
            && char.IsAsciiDigit(raw[^4])
            && char.IsAsciiDigit(raw[^2])
            && char.IsAsciiDigit(raw[^1]);
    }
}
