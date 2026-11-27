using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Orders;

public sealed record CreateOrderShipmentRequest(
    string CarrierName,
    string? TrackingCode = null,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? ShippingCostVnd = null,
    string? Note = null);

public sealed class PatchShipmentRequest
{
    public string? TrackingCode { get; init; }

    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long? ShippingCostVnd { get; init; }

    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long? ReturnCostVnd { get; init; }
}

public sealed record RecordShipmentEventRequest(
    string Status,
    DateTimeOffset OccurredAt,
    string? Note = null,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? ReturnCostVnd = null);

public static class ShipmentEndpoints
{
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
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapShipments(this IEndpointRouteBuilder endpoints)
    {
        // 1. POST /api/v1/admin/orders/{id}/shipments (05-api.md:186 — ASSUMPTION-P208-01 = A -> 201 Created)
        endpoints.MapPost("/api/v1/admin/orders/{id:guid}/shipments", async (
            Guid id,
            HttpContext http,
            ICommandHandler<CreateOrderShipment, ShipmentDetailView> handler,
            CancellationToken ct) =>
        {
            var (input, parseError) = await ReadStrictJsonAsync<CreateOrderShipmentRequest>(http, requiredOffsetDateProperty: null, ct);
            if (parseError is not null) return parseError;

            var result = await handler.Handle(new CreateOrderShipment(
                id,
                input!.CarrierName,
                input.TrackingCode,
                input.ShippingCostVnd,
                input.Note,
                ActorId(http)), ct);

            return result.IsSuccess
                ? Results.Created($"/api/v1/admin/shipments/{result.Value.Id}", result.Value)
                : result.Error.ToProblem(http);
        })
        .WithTags("Shipments")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<CreateOrderShipmentRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<ShipmentDetailView>(StatusCodes.Status201Created)
        .WriteErrors()
        .RequireJsonContentType();

        // 2. PATCH /api/v1/admin/shipments/{id} (05-api.md:187 — partial update; null clears column)
        endpoints.MapPatch("/api/v1/admin/shipments/{id:guid}", async (
            Guid id,
            HttpContext http,
            ICommandHandler<PatchShipment, ShipmentDetailView> handler,
            CancellationToken ct) =>
        {
            var (changes, parseError) = await ReadPatchChangesAsync(http, ct);
            if (parseError is not null) return parseError;

            var result = await handler.Handle(new PatchShipment(id, changes!, ActorId(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Shipments")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<PatchShipmentRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<ShipmentDetailView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();

        // 3. POST /api/v1/admin/shipments/{id}/events (05-api.md:188 — GAP-P208-01=A, GAP-P208-02=A)
        endpoints.MapPost("/api/v1/admin/shipments/{id:guid}/events", async (
            Guid id,
            HttpContext http,
            ICommandHandler<RecordShipmentEvent, ShipmentDetailView> handler,
            CancellationToken ct) =>
        {
            var (input, parseError) = await ReadStrictJsonAsync<RecordShipmentEventRequest>(http, requiredOffsetDateProperty: "occurredAt", ct);
            if (parseError is not null) return parseError;

            var result = await handler.Handle(new RecordShipmentEvent(
                id,
                input!.Status,
                input.OccurredAt,
                input.Note,
                input.ReturnCostVnd,
                ActorId(http)), ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        })
        .WithTags("Shipments")
        .RequireAuthorization(AdminSecurity.Ready, "orders.write")
        .WithJsonRequest<RecordShipmentEventRequest>()
        .WithMetadata(new RequireCsrfHeader())
        .Produces<ShipmentDetailView>(StatusCodes.Status200OK)
        .WriteErrors()
        .RequireJsonContentType();
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

    private static Guid ActorId(HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult UnsupportedMediaType(HttpContext http) =>
        AdminSecurity.Problem(http, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

    private static IResult InvalidBody(HttpContext http) =>
        new Error("validation_failed", "Nội dung yêu cầu không hợp lệ.", ErrorType.Validation).ToProblem(http);

    private static async Task<(byte[]? Bytes, IResult? Error)> ReadBufferAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return (null, UnsupportedMediaType(http));
        if (http.Request.ContentLength > MaxBodyBytes)
            return (null, InvalidBody(http));

        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (int read; (read = await http.Request.Body.ReadAsync(buffer, ct)) > 0;)
        {
            if (data.Length + read > MaxBodyBytes)
                return (null, InvalidBody(http));
            data.Write(buffer, 0, read);
        }

        if (data.Length == 0)
            return (null, InvalidBody(http));

        return (data.ToArray(), null);
    }

    private static async Task<(T? Value, IResult? Error)> ReadStrictJsonAsync<T>(
        HttpContext http,
        string? requiredOffsetDateProperty,
        CancellationToken ct)
        where T : class
    {
        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (null, readError);

        try
        {
            using var doc = JsonDocument.Parse(bytes!, StrictDocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, InvalidBody(http));

            if (requiredOffsetDateProperty is not null)
            {
                if (!root.TryGetProperty(requiredOffsetDateProperty, out var dateEl)
                    || dateEl.ValueKind != JsonValueKind.String
                    || !HasExplicitIsoOffset(dateEl.GetString()))
                {
                    return (null, InvalidBody(http));
                }
            }

            var value = root.Deserialize<T>(StrictJsonOptions);
            return value is null ? (null, InvalidBody(http)) : (value, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody(http));
        }
    }

    private static async Task<(PatchShipmentChanges? Changes, IResult? Error)> ReadPatchChangesAsync(
        HttpContext http,
        CancellationToken ct)
    {
        var (bytes, readError) = await ReadBufferAsync(http, ct);
        if (readError is not null) return (null, readError);

        try
        {
            using var doc = JsonDocument.Parse(bytes!, StrictDocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, InvalidBody(http));

            var hasTrackingCode = false;
            string? trackingCode = null;
            var hasShippingCost = false;
            long? shippingCost = null;
            var hasReturnCost = false;
            long? returnCost = null;

            foreach (var prop in root.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "trackingCode":
                        hasTrackingCode = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            trackingCode = null;
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                            trackingCode = prop.Value.GetString();
                        else
                            return (null, InvalidBody(http));
                        break;

                    case "shippingCostVnd":
                        hasShippingCost = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            shippingCost = null;
                        else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var parsedShip))
                            shippingCost = parsedShip;
                        else
                            return (null, InvalidBody(http));
                        break;

                    case "returnCostVnd":
                        hasReturnCost = true;
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            returnCost = null;
                        else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var parsedReturn))
                            returnCost = parsedReturn;
                        else
                            return (null, InvalidBody(http));
                        break;

                    default:
                        return (null, InvalidBody(http));
                }
            }

            if (!hasTrackingCode && !hasShippingCost && !hasReturnCost)
                return (null, InvalidBody(http));

            return (new PatchShipmentChanges(
                hasTrackingCode,
                trackingCode,
                hasShippingCost,
                shippingCost,
                hasReturnCost,
                returnCost), null);
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
}
