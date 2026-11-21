using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Idempotency;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Results;

namespace TeaPremium.Api.Inventory;

public sealed record CreateReceiptLineRequest(Guid VariantId,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Quantity,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long UnitCostVnd)
{
    internal ReceiptLineInput ToInput() => new(VariantId, Quantity, UnitCostVnd);
}
public sealed record EditReceiptLineRequest(Guid VariantId,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Quantity,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? UnitCostVnd = null)
{
    internal ReceiptLineInput ToInput() => new(VariantId, Quantity, UnitCostVnd);
}

public sealed record CreateReceiptRequest(string? SupplierName = null, string? Note = null,
    DateTimeOffset? ReceivedAt = null, CreateReceiptLineRequest[]? Lines = null)
{
    internal ReceiptDraft Draft => new(SupplierName, Note, ReceivedAt?.UtcDateTime, Lines?.Select(x => x.ToInput()).ToArray());
}
public sealed record EditReceiptRequest(string RowVersion, string? SupplierName = null, string? Note = null,
    DateTimeOffset? ReceivedAt = null, EditReceiptLineRequest[]? Lines = null)
{
    internal ReceiptDraft Draft => new(SupplierName, Note, ReceivedAt?.UtcDateTime, Lines?.Select(x => x.ToInput()).ToArray());
}
public sealed record InventoryDocumentVersion(string RowVersion);
public sealed record CreateStocktakeRequest(string? Note = null);
public sealed record CountStocktakeRequest(string RowVersion,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int CountedQty);
public sealed record InventoryAdjustmentRequest(Guid VariantId,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int QuantityDelta, string Type, string Reason);

public static class InventoryDocumentEndpoints
{
    private const string Receipts = "/api/v1/admin/receipts";
    private const string Stocktakes = "/api/v1/admin/stocktakes";
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapInventoryDocuments(this IEndpointRouteBuilder endpoints)
    {
        var receipts = endpoints.MapGroup(Receipts).WithTags("Receipts").RequireAuthorization(AdminSecurity.Ready, "inventory.write");
        receipts.MapGet("", async (HttpContext http, IInventoryDocumentStore db, CancellationToken ct, int page = 1, int pageSize = 20, string? status = null) =>
            !ValidPage(page, pageSize) || status is not (null or "draft" or "posted" or "cancelled")
                ? Invalid("filter", http) : Results.Ok(await db.ReceiptsAsync(page, pageSize, status, ct)))
            .Produces<CatalogPage<ReceiptSummary>>().ReadErrors();
        receipts.MapGet("/{id:guid}", async (Guid id, HttpContext http, IInventoryDocumentStore db, CancellationToken ct) =>
            await db.ReceiptAsync(id, false, ct) is { } doc ? Results.Ok(doc.View(Costs(http))) : Missing(http))
            .Produces<ReceiptView>().ReadErrors();
        receipts.MapPost("", async (HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, json) = await ReadAsync<CreateReceiptRequest>(http.Request, ct);
            if (input is null || ExplicitNullLines(json) || input.Lines?.Any(x => x is null) == true) return Invalid("body", http);
            var result = await service.CreateReceiptAsync(input.Draft, Actor(http), Costs(http), ct);
            return result.IsSuccess ? Results.Created(Receipts + "/" + result.Value.Document.Id, result.Value) : result.Error.ToProblem(http);
        }).Write<CreateReceiptRequest, ReceiptView>(201);
        receipts.MapPatch("/{id:guid}", async (Guid id, HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, json) = await ReadAsync<EditReceiptRequest>(http.Request, ct);
            if (input is null || ExplicitNullLines(json) || input.Lines?.Any(x => x is null) == true) return Invalid("body", http);
            return Respond(await service.EditReceiptAsync(id, input.RowVersion, new(input.Draft,
                json.TryGetProperty("supplierName", out _), json.TryGetProperty("note", out _), json.TryGetProperty("receivedAt", out _)),
                Costs(http), ct), http);
        }).Write<EditReceiptRequest, ReceiptView>();
        receipts.MapPost("/{id:guid}/post", async (Guid id, HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, _) = await ReadAsync<InventoryDocumentVersion>(http.Request, ct);
            if (input is null) return (object)Invalid("body", http);
            var result = await service.PostReceiptAsync(id, input.RowVersion, Actor(http), ct);
            return result.IsSuccess ? (object)new IdempotentResponse(200, result.Value) : result.Error.ToProblem(http);
        }).Write<InventoryDocumentVersion, ReceiptPosted>()
            .WithIdempotency(new("inventory.receipt.post.v1", http => AdminSecurity.Scheme + ":" + Actor(http).ToString("D"), SafePostResponse))
            .AddOpenApiOperationTransformer((o, _, _) =>
            {
                o.Parameters ??= [];
                o.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                return Task.CompletedTask;
            });
        receipts.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, _) = await ReadAsync<InventoryDocumentVersion>(http.Request, ct);
            return input is null ? Invalid("body", http) : Respond(await service.CancelReceiptAsync(id, input.RowVersion, Costs(http), ct), http);
        }).Write<InventoryDocumentVersion, ReceiptView>();

        endpoints.MapPost("/api/v1/admin/inventory/adjustments", async (HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, _) = await ReadAsync<InventoryAdjustmentRequest>(http.Request, ct);
            return input is null ? Invalid("body", http) : Respond(await service.AdjustAsync(input.VariantId, input.QuantityDelta,
                input.Type, input.Reason, Actor(http), Has(http, "inventory.approve"), ct), http);
        }).WithTags("Inventory").RequireAuthorization(AdminSecurity.Ready, "inventory.write")
            .Write<InventoryAdjustmentRequest, AdjustmentView>();

        var counts = endpoints.MapGroup(Stocktakes).WithTags("Stocktakes").RequireAuthorization(AdminSecurity.Ready, "inventory.write");
        counts.MapGet("", async (HttpContext http, IInventoryDocumentStore db, CancellationToken ct, int page = 1, int pageSize = 20, string? status = null) =>
            !ValidPage(page, pageSize) || status is not (null or "counting" or "submitted" or "approved" or "cancelled")
                ? Invalid("filter", http) : Results.Ok(await db.StocktakesAsync(page, pageSize, status, ct)))
            .Produces<CatalogPage<StocktakeSummary>>().ReadErrors();
        counts.MapGet("/{id:guid}", async (Guid id, HttpContext http, IInventoryDocumentStore db, CancellationToken ct) =>
            await db.StocktakeAsync(id, false, ct) is { } doc ? Results.Ok(doc.View) : Missing(http))
            .Produces<StocktakeView>().ReadErrors();
        counts.MapPost("", async (HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, _) = await ReadAsync<CreateStocktakeRequest>(http.Request, ct);
            if (input is null) return Invalid("body", http);
            var result = await service.CreateStocktakeAsync(input.Note, Actor(http), ct);
            return result.IsSuccess ? Results.Created(Stocktakes + "/" + result.Value.Document.Id, result.Value) : result.Error.ToProblem(http);
        }).Write<CreateStocktakeRequest, StocktakeView>(201);
        counts.MapPut("/{id:guid}/lines/{variantId:guid}", async (Guid id, Guid variantId, HttpContext http, InventoryDocuments service, CancellationToken ct) =>
        {
            var (input, _) = await ReadAsync<CountStocktakeRequest>(http.Request, ct);
            return input is null ? Invalid("body", http) : Respond(await service.CountAsync(id, input.RowVersion, variantId, input.CountedQty, Actor(http), ct), http);
        }).Write<CountStocktakeRequest, StocktakeView>();

        foreach (var action in new[] { "submit", "approve", "cancel" })
        {
            var route = counts.MapPost("/{id:guid}/" + action, async (Guid id, HttpContext http, InventoryDocuments service, CancellationToken ct) =>
            {
                var (input, _) = await ReadAsync<InventoryDocumentVersion>(http.Request, ct);
                return input is null ? Invalid("body", http) : Respond(await service.TransitionStocktakeAsync(id, input.RowVersion, action, Actor(http), ct), http);
            }).Write<InventoryDocumentVersion, StocktakeView>();
            if (action == "approve") route.RequireAuthorization("inventory.approve");
        }
    }

    internal static Task TransformOpenApiAsync(OpenApiDocument document, Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;
        foreach (var name in new[] { nameof(CreateReceiptRequest), nameof(EditReceiptRequest), nameof(CreateReceiptLineRequest),
            nameof(EditReceiptLineRequest), nameof(InventoryDocumentVersion), nameof(CreateStocktakeRequest),
            nameof(CountStocktakeRequest), nameof(InventoryAdjustmentRequest) })
        {
            ((OpenApiSchema)schemas[name]).AdditionalPropertiesAllowed = false;
        }
        foreach (var name in new[] { nameof(CreateReceiptRequest), nameof(EditReceiptRequest) })
        {
            // Omission and explicit null are distinct at the raw JSON boundary.
            var lines = (OpenApiSchema)schemas[name].Properties!["lines"].CreateShallowCopy();
            lines.Type = JsonSchemaType.Array;
            lines.MaxItems = InventoryDocuments.MaxLines;
            schemas[name].Properties!["lines"] = lines;
        }
        SetMinimum(nameof(CreateReceiptLineRequest), "quantity", "1");
        SetMinimum(nameof(EditReceiptLineRequest), "quantity", "1");
        SetMinimum(nameof(CreateReceiptLineRequest), "unitCostVnd", "0");
        SetMinimum(nameof(EditReceiptLineRequest), "unitCostVnd", "0");
        SetMinimum(nameof(CountStocktakeRequest), "countedQty", "0");
        var adjustment = schemas[nameof(InventoryAdjustmentRequest)].Properties!;
        var kind = (OpenApiSchema)adjustment["type"].CreateShallowCopy();
        kind.Enum = [System.Text.Json.Nodes.JsonValue.Create("adjustment"), System.Text.Json.Nodes.JsonValue.Create("damage")];
        adjustment["type"] = kind;
        return Task.CompletedTask;

        void SetMinimum(string schema, string field, string minimum)
        {
            var value = (OpenApiSchema)schemas[schema].Properties![field].CreateShallowCopy();
            // The exporter still adds a numeric-string alternative for nullable integers.
            value.Type &= ~JsonSchemaType.String;
            value.Pattern = null;
            value.Minimum = minimum;
            schemas[schema].Properties![field] = value;
        }
    }

    private static bool ExplicitNullLines(JsonElement json) => json.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Null;
    private static bool ValidPage(int page, int pageSize) => page is >= 1 and <= int.MaxValue / 100 && pageSize is >= 1 and <= 100;
    private static bool Costs(HttpContext http) => Has(http, "costs.read");
    private static bool Has(HttpContext http, string permission) => http.User.HasClaim("permission", permission);
    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Invalid(string field, HttpContext http) => InventoryDocuments.Invalid(field).ToProblem(http);
    private static IResult Missing(HttpContext http) => new Error("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound).ToProblem(http);
    private static IResult Respond<T>(Result<T> result, HttpContext http) => result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    private static RouteHandlerBuilder ReadErrors(this RouteHandlerBuilder route) =>
        route.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

    private static RouteHandlerBuilder Write<TInput, TOutput>(this RouteHandlerBuilder route, int status = 200) where TInput : notnull =>
        route.Accepts<TInput>("application/json").WithMetadata(new RequireCsrfHeader()).Produces<TOutput>(status)
            .ReadErrors().ProducesProblem(409).ProducesProblem(415);

    private static async Task<(T?, JsonElement)> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType()) return (null, default);
        const int maxBody = 256 * 1024;
        if (request.ContentLength > maxBody) return (null, default);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (int read; (read = await request.Body.ReadAsync(buffer, ct)) > 0;)
        {
            if (data.Length + read > maxBody) return (null, default);
            data.Write(buffer, 0, read);
        }
        try
        {
            using var doc = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return (null, default);
            if (json.TryGetProperty("receivedAt", out var received) && received.ValueKind == JsonValueKind.String
                && received.GetString() is { } date && !date.EndsWith('Z')
                && (date.Length < 6 || date[^6] is not ('+' or '-') || date[^3] != ':'))
                return (null, default);
            return (json.Deserialize<T>(Input), json.Clone());
        }
        catch (JsonException) { return (null, default); }
    }

    private static bool SafePostResponse(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 3
        && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var parsed) && parsed != Guid.Empty
        && value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.ValueEquals("posted")
        && value.TryGetProperty("rowVersion", out var version) && version.ValueKind == JsonValueKind.String
        && CatalogVersions.TryDecode(version.GetString(), out var decoded) && version.ValueEquals(CatalogVersions.Encode(decoded));
}
