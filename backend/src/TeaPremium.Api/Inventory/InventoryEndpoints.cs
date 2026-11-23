using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using TeaPremium.Api.Catalog;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Inventory;

public static class InventoryEndpoints
{
    private static readonly string[] OpeningColumns = ["sku", "quantity", "unit_cost_vnd"];

    public static void MapInventory(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/inventory").WithTags("Inventory").RequireAuthorization(AdminSecurity.Ready);
        group.MapGet("", ListAsync).RequireAuthorization("inventory.read")
            .WithDescription("Restricted readers receive only binary availability and cannot use status=low. Cost fields require costs.read.")
            .Produces<CatalogPage<InventoryStockView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        group.MapGet("/alerts", AlertsAsync).RequireAuthorization("inventory.read", "inventory.write")
            .WithDescription("Current low-stock variants, ledger reconciliation mismatches, and dead InventoryAlert outbox count. Never includes cost fields.")
            .Produces<InventoryAlertsView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        group.MapGet("/{variantId:guid}/movements", MovementsAsync).RequireAuthorization("inventory.read", "inventory.write")
            .WithDescription("Filter occurredAt from (inclusive) to (exclusive), using offset timestamps or Vietnam dates (yyyy-MM-dd).")
            .Produces<CatalogPage<InventoryMovementView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        group.MapPost("/opening-import", OpeningAsync).RequireAuthorization("inventory.approve")
            .WithMetadata(new RequireCsrfHeader())
            .WithDescription("Preview validates all rows. Apply requires a preview token, the same file, and the same staff identity. All rows commit together.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Parameters!.Single(x => x.Name == "dryRun") is OpenApiParameter mode)
                {
                    mode.Required = true;
                    mode.Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean };
                }
                if (operation.Parameters!.Single(x => x.Name == OpeningPreviewToken.Header) is OpenApiParameter token)
                    token.Description = "Required when dryRun=false. Use previewToken from the dry-run response within 30 minutes.";
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["multipart/form-data"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Required = new HashSet<string>(StringComparer.Ordinal) { "file" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                                }
                            }
                        }
                    }
                };
                return Task.CompletedTask;
            })
            .Produces<OpeningResult>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(413).ProducesProblem(415);
    }

    private static async Task<IResult> ListAsync(HttpContext http, IQueryHandler<ListInventory, CatalogPage<InventoryStockView>> handler,
        CancellationToken ct, int page = 1, int pageSize = 20, string? q = null, string? status = null, Guid? type = null)
    {
        var result = await handler.Handle(new(page, pageSize, q, status, type, Has(http, "inventory.write"), Has(http, "costs.read")), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    }

    private static async Task<IResult> AlertsAsync(HttpContext http, IQueryHandler<GetInventoryAlerts, InventoryAlertsView> handler,
        CancellationToken ct, int limit = 50)
    {
        var result = await handler.Handle(new(limit), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    }

    private static async Task<IResult> MovementsAsync(Guid variantId, HttpContext http, IQueryHandler<ListMovements, CatalogPage<InventoryMovementView>> handler,
        CancellationToken ct, int page = 1, int pageSize = 20, string? type = null, string? from = null, string? to = null)
    {
        if (!TryBoundary(from, out var start)) return Invalid("from", http);
        if (!TryBoundary(to, out var end)) return Invalid("to", http);
        var result = await handler.Handle(new(variantId, page, pageSize, type, start, end, Has(http, "costs.read")), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
    }

    private static async Task<IResult> OpeningAsync(HttpContext http, string? dryRun, [FromHeader(Name = OpeningPreviewToken.Header)] string? previewToken,
        IDataProtectionProvider protection, IClock clock, IQueryHandler<PreviewOpening, OpeningResult> preview,
        ICommandHandler<ApplyOpening, OpeningResult> apply, CancellationToken ct)
    {
        if (http.Request.Query["dryRun"].Count != 1 || dryRun is not ("true" or "false")) return Invalid("dryRun", http);
        var staff = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var tokens = new OpeningPreviewToken(protection, clock);
        OpeningInput[] lines;
        byte[] digest;
        try
        {
            (lines, digest) = await ProductImportEndpoint.ReadUploadAsync(http.Request, (data, filename) =>
            {
                var hash = SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length));
                var rows = ProductImportFile.Read(data, filename, OpeningColumns);
                return (rows.Select(x => new OpeningInput(x.Row, x.Cell("sku"), x.Cell("quantity"), x.Cell("unit_cost_vnd"))).ToArray(), hash);
            }, ct);
        }
        catch (ProductImportFileException error) { return ProductImportEndpoint.FileProblem(error, http); }
        catch (InvalidDataException) { return Invalid("file", http); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return AdminSecurity.Problem(http, 413, "file_limit_exceeded"); }
        catch (IOException) when (!ct.IsCancellationRequested) { return Invalid("file", http); }

        if (dryRun == "true")
        {
            var result = await preview.Handle(new(lines, staff), ct);
            if (!result.IsSuccess) return result.Error.ToProblem(http);
            var response = result.Value;
            if (response.InvalidRows == 0)
                response = response with { PreviewToken = tokens.Protect(staff, digest, response.Rows.Select(x => x.VariantId!.Value).ToArray()) };
            return Results.Ok(response);
        }

        var variants = http.Request.Headers[OpeningPreviewToken.Header].Count == 1 ? tokens.Verify(previewToken, staff, digest, lines.Length) : null;
        if (variants is null) return AdminSecurity.Problem(http, 400, "opening_preview_invalid");
        var applied = await apply.Handle(new(lines, variants, staff), ct);
        return applied.IsSuccess ? Results.Ok(applied.Value) : applied.Error.ToProblem(http);
    }

    private static bool Has(HttpContext http, string permission) => http.User.HasClaim("permission", permission);

    private static IResult Invalid(string field, HttpContext http) =>
        new Error("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
            new Dictionary<string, string[]> { [field] = ["Giá trị không hợp lệ."] }).ToProblem(http);

    private static bool TryBoundary(string? value, out DateTime? utc)
    {
        utc = null;
        if (value is null) return true;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            if (date == DateOnly.MinValue) return false;
            utc = BusinessCalendar.ToUtc(date, TimeOnly.MinValue).UtcDateTime;
            return true;
        }
        if (value.Length > 35 || !(value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':')
            || !System.Text.Json.JsonSerializer.SerializeToElement(value).TryGetDateTimeOffset(out var timestamp))
            return false;
        utc = timestamp.UtcDateTime;
        return true;
    }
}
