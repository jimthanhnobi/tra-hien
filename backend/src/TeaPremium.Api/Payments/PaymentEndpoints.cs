using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Idempotency;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Api.Payments;

public sealed record RecordOrderPaymentRequest(
    string Method,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long AmountVnd,
    DateTimeOffset ReceivedAt,
    string? BankReference = null,
    string? Note = null);

public sealed record VoidPaymentRequest(string Reason);

public sealed record CreateOrderRefundRequest(
    string Kind,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long AmountVnd,
    string Method,
    string Reason,
    DateTimeOffset RefundedAt,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long ShippingAmountVnd = 0,
    string? BankReference = null,
    Guid? ReturnRequestId = null);

public static class PaymentEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapPayments(this IEndpointRouteBuilder endpoints)
    {
        // 1. POST /api/v1/admin/orders/{id}/payments [idem] (05-api.md:191)
        endpoints.MapPost("/api/v1/admin/orders/{id:guid}/payments", async (
            Guid id,
            HttpContext http,
            ICommandHandler<RecordOrderPayment, PaymentRecordedView> handler,
            CancellationToken ct) =>
        {
            var (input, parseError) = await ReadStrictJsonAsync<RecordOrderPaymentRequest>(http, "receivedAt", ct);
            if (parseError is not null) return (object)parseError;

            var result = await handler.Handle(new RecordOrderPayment(
                id,
                input!.Method,
                input.AmountVnd,
                input.ReceivedAt,
                input.BankReference,
                input.Note,
                ActorId(http)), ct);

            return result.IsSuccess
                ? (object)new IdempotentResponse(StatusCodes.Status200OK, result.Value)
                : result.Error.ToProblem(http);
        })
        .WithTags("Payments")
        .RequireAuthorization(AdminSecurity.Ready, "payments.write")
        .WithJsonRequest<RecordOrderPaymentRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<PaymentRecordedView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType()
        .WithIdempotency(new(
            "payments.order.record.v1",
            http => AdminSecurity.Scheme + ":" + ActorId(http).ToString("D"),
            SafePaymentRecordedResponse))
        .AddIdempotencyKeyHeaderParameter();

        // 2. POST /api/v1/admin/payments/{id}/void (05-api.md:192 — owner only)
        endpoints.MapPost("/api/v1/admin/payments/{id:guid}/void", async (
            Guid id,
            HttpContext http,
            ICommandHandler<VoidPayment, PaymentVoidedView> handler,
            CancellationToken ct) =>
        {
            var (input, parseError) = await ReadStrictJsonAsync<VoidPaymentRequest>(http, null, ct);
            if (parseError is not null) return parseError;

            var result = await handler.Handle(new VoidPayment(id, input!.Reason, ActorId(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Payments")
        .RequireAuthorization(AdminSecurity.Owner)
        .WithJsonRequest<VoidPaymentRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<PaymentVoidedView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();

        // 3. GET /api/v1/admin/payments/awaiting?type=bank_transfer|cod&carrier= (05-api.md:193)
        endpoints.MapGet("/api/v1/admin/payments/awaiting", async (
            HttpContext http,
            IQueryHandler<GetAwaitingPayments, AwaitingPaymentsPage> handler,
            CancellationToken ct,
            string? type = null,
            string? carrier = null,
            int page = 1,
            int pageSize = 20) =>
        {
            var result = await handler.Handle(new GetAwaitingPayments(type ?? "", carrier, page, pageSize), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Payments")
        .RequireAuthorization(AdminSecurity.Ready, "payments.write")
        .Produces<AwaitingPaymentsPage>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 4. POST /api/v1/admin/orders/{id}/refunds [idem] (05-api.md:195 — refunds.write / owner)
        endpoints.MapPost("/api/v1/admin/orders/{id:guid}/refunds", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CreateOrderRefund, RefundRecordedView> handler,
            CancellationToken ct) =>
        {
            var (input, parseError) = await ReadStrictJsonAsync<CreateOrderRefundRequest>(http, "refundedAt", ct);
            if (parseError is not null) return (object)parseError;

            var result = await handler.Handle(new CreateOrderRefund(
                id,
                input!.Kind,
                input.AmountVnd,
                input.ShippingAmountVnd,
                input.Method,
                input.BankReference,
                input.Reason,
                input.ReturnRequestId,
                input.RefundedAt,
                ActorId(http)), ct);

            return result.IsSuccess
                ? (object)new IdempotentResponse(StatusCodes.Status200OK, result.Value)
                : result.Error.ToProblem(http);
        })
        .WithTags("Payments")
        .RequireAuthorization(AdminSecurity.Ready, "refunds.write")
        .WithJsonRequest<CreateOrderRefundRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<RefundRecordedView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType()
        .WithIdempotency(new(
            "payments.order.refund.v1",
            http => AdminSecurity.Scheme + ":" + ActorId(http).ToString("D"),
            SafeRefundRecordedResponse))
        .AddIdempotencyKeyHeaderParameter();
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

    private static IResult UnsupportedMediaType(HttpContext http) =>
        AdminSecurity.Problem(http, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

    private static IResult InvalidBody(HttpContext http) =>
        new Error("validation_failed", "Nội dung yêu cầu không hợp lệ.", ErrorType.Validation).ToProblem(http);

    private static async Task<(T? Value, IResult? Error)> ReadStrictJsonAsync<T>(
        HttpContext http,
        string? requiredOffsetDateProperty,
        CancellationToken ct)
        where T : class
    {
        var request = http.Request;
        if (!request.HasJsonContentType()) return (null, UnsupportedMediaType(http));
        if (request.ContentLength > MaxBodyBytes) return (null, InvalidBody(http));

        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (int read; (read = await request.Body.ReadAsync(buffer, ct)) > 0;)
        {
            if (data.Length + read > MaxBodyBytes) return (null, InvalidBody(http));
            data.Write(buffer, 0, read);
        }

        if (data.Length == 0) return (null, InvalidBody(http));

        try
        {
            using var doc = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, InvalidBody(http));

            if (requiredOffsetDateProperty is not null)
            {
                if (!root.TryGetProperty(requiredOffsetDateProperty, out var dateEl)
                    || dateEl.ValueKind != JsonValueKind.String
                    || !HasExplicitIsoOffset(dateEl.GetString()))
                    return (null, InvalidBody(http));
            }

            var value = root.Deserialize<T>(InputOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
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

    private static bool SafePaymentRecordedResponse(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 18
        && IsNonEmptyGuid(value, "paymentId")
        && IsNonEmptyGuid(value, "orderId")
        && IsValidString(value, "orderCode", OrderCode.IsValid)
        && IsValidString(value, "method", PaymentMethod.IsManualRecordable)
        && IsValidString(value, "provider", PaymentProvider.IsValid)
        && IsValidString(value, "status", PaymentRecordStatus.IsValid)
        && IsPositiveInt64(value, "amountVnd")
        && IsDateTimeOffset(value, "receivedAt")
        && IsDateTimeOffset(value, "confirmedAt")
        && IsValidString(value, "orderStatus", OrderStatus.IsValid)
        && IsValidString(value, "orderPaymentStatus", OrderPaymentStatus.IsValid)
        && IsNonNegativeInt64(value, "paidVnd")
        && IsNonNegativeInt64(value, "refundedVnd")
        && IsNonNegativeInt64(value, "netPaidVnd")
        && IsBoolean(value, "isOverpaid")
        && IsBoolean(value, "needsRefund")
        && IsBoolean(value, "autoConfirmed")
        && IsCanonicalRowVersion(value, "orderRowVersion");

    private static bool SafeRefundRecordedResponse(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 18
        && IsNonEmptyGuid(value, "refundId")
        && IsNonEmptyGuid(value, "orderId")
        && IsValidString(value, "orderCode", OrderCode.IsValid)
        && IsNullableNonEmptyGuid(value, "returnRequestId")
        && IsNullableValidString(value, "returnRequestStatus", ReturnRequestStatus.IsValid)
        && IsValidString(value, "kind", RefundKind.IsValid)
        && IsValidString(value, "method", RefundMethod.IsValid)
        && IsPositiveInt64(value, "amountVnd")
        && IsNonNegativeInt64(value, "shippingAmountVnd")
        && IsDateTimeOffset(value, "refundedAt")
        && IsValidString(value, "orderStatus", OrderStatus.IsValid)
        && IsValidString(value, "orderPaymentStatus", OrderPaymentStatus.IsValid)
        && IsNonNegativeInt64(value, "paidVnd")
        && IsNonNegativeInt64(value, "refundedVnd")
        && IsNonNegativeInt64(value, "netPaidVnd")
        && IsBoolean(value, "isOverpaid")
        && IsBoolean(value, "needsRefund")
        && IsCanonicalRowVersion(value, "orderRowVersion");

    private static bool IsNonEmptyGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && prop.TryGetGuid(out var parsed)
        && parsed != Guid.Empty;

    private static bool IsNullableNonEmptyGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && (prop.ValueKind == JsonValueKind.Null
            || (prop.ValueKind == JsonValueKind.String && prop.TryGetGuid(out var parsed) && parsed != Guid.Empty));

    private static bool IsValidString(JsonElement root, string name, Func<string?, bool> predicate) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && predicate(prop.GetString());

    private static bool IsNullableValidString(JsonElement root, string name, Func<string?, bool> predicate) =>
        root.TryGetProperty(name, out var prop)
        && (prop.ValueKind == JsonValueKind.Null
            || (prop.ValueKind == JsonValueKind.String && predicate(prop.GetString())));

    private static bool IsPositiveInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt64(out var parsed)
        && parsed > 0;

    private static bool IsNonNegativeInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt64(out var parsed)
        && parsed >= 0;

    private static bool IsBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && prop.TryGetDateTimeOffset(out _);

    private static bool IsCanonicalRowVersion(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && CatalogVersions.TryDecode(prop.GetString(), out var decoded)
        && prop.ValueEquals(CatalogVersions.Encode(decoded));
}
