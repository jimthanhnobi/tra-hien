using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using TeaPremium.Application.Results;
using Microsoft.Net.Http.Headers;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Api.Catalog;

public sealed record ProductImportUpload(IFormFile File);

internal static partial class ProductImportEndpoint
{
    [LoggerMessage(EventId = 34, Level = LogLevel.Warning, Message = "Product import stopped at row {Row}. Trace {TraceId}.")]
    private static partial void ImportStopped(ILogger logger, int row, string traceId);

    public static void MapImport(this RouteGroupBuilder group)
    {
        group.MapPost("/import", ImportAsync)
            .Accepts<ProductImportUpload>("multipart/form-data")
            .WithMetadata(new RequireCsrfHeader())
            .RequireAuthorization("catalog.write")
            .Produces<ProductImportResult>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413).ProducesProblem(415);
    }

    private static async Task<IResult> ImportAsync(HttpContext http, string? dryRun, IServiceScopeFactory scopes,
        ILoggerFactory logger, CancellationToken ct)
    {
        if (http.Request.Query["dryRun"].Count != 1 || dryRun is not ("true" or "false"))
            return FileProblem(ProductImportFile.Invalid(1, "dryRun", "invalid_mode", "Chọn dryRun=true hoặc dryRun=false."));
        ImportProductLine[] lines;
        try
        {
            lines = await ReadUploadAsync(http.Request, ProductImportFile.Read, ct);
        }
        catch (ProductImportFileException error) { return FileProblem(error); }
        catch (InvalidDataException) { return FileProblem(ProductImportFile.Invalid(1, "file", "invalid_multipart", "Yêu cầu multipart không hợp lệ.")); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return FileProblem(ProductImportFile.Limit(1, "file", "Tệp vượt 5 MiB.")); }
        catch (IOException) when (!ct.IsCancellationRequested)
        { return FileProblem(ProductImportFile.Invalid(1, "file", "invalid_multipart", "Yêu cầu tải tệp bị ngắt hoặc không hợp lệ.")); }

        var actor = ProductEndpoints.Actor(http);
        var explicitSlugs = lines.Select(l => l.Cell("product_slug")).Where(TaxonomyRules.ValidSlug).ToHashSet(StringComparer.Ordinal);
        var manual = lines.Where(l => ProductRules.ValidSku(l.Cell("sku"))).ToLookup(l => l.Cell("sku"), StringComparer.Ordinal);
        var duplicateRows = manual.Where(g => g.Skip(1).Any()).SelectMany(g => g).Select(l => l.Row).ToHashSet();
        var reserveSkus = actor.IsOwner ? manual.Select(g => g.Key).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var reservedSlugs = new HashSet<string>(explicitSlugs, StringComparer.Ordinal);
        var groups = lines.GroupBy(l => (Slug: l.Cell("product_slug"), Unique: l.Cell("product_slug").Length > 0 ? 0 : l.Row))
            .Select(g => g.ToArray()).ToArray();
        var results = new List<ProductImportGroupResult>();
        var planner = http.RequestServices.GetRequiredService<IQueryHandler<PlanProductImportGroup, ProductImportPlan>>();
        var stop = false;
        foreach (var group in groups)
        {
            var duplicates = group.Where(l => duplicateRows.Contains(l.Row))
                .Select(l => new ProductImportError(l.Row, "sku", "duplicate_sku_in_file", "SKU xuất hiện nhiều lần trong tệp.")).ToArray();
            var input = new PlanProductImportGroup(group, actor, reservedSlugs, duplicates);
            if (stop)
            {
                results.Add(Failed(group, "notProcessed", "import_stopped", "Chưa xử lý vì cần xác minh nhóm trước."));
                continue;
            }
            if (dryRun == "true")
            {
                var plan = (await planner.Handle(input, ct)).Value;
                results.Add(plan.Result);
                if (plan.Product is { } draft) reservedSlugs.Add(draft.Slug);
                continue;
            }
            if (duplicates.Length > 0)
            {
                // Find every conflicting row in the file before issuing any write command.
                results.Add((await planner.Handle(input, ct)).Value.Result);
                continue;
            }

            try
            {
                Result<ProductImportPlan> applied;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyProductImportGroup, ProductImportPlan>>();
                    applied = await handler.Handle(new(input, reserveSkus), ct);
                }
                results.Add(applied.IsSuccess ? applied.Value.Result : Failed(group, "failed", applied.Error.Code, applied.Error.Message, applied.Error.ValidationErrors));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Commit/rollback acknowledgement may have been lost. An independent scope
                // preserves prior outcomes and no later command or retry is issued.
                ImportStopped(logger.CreateLogger("ProductImport"), group[0].Row, http.TraceIdentifier);
                results.Add(Failed(group, "unknown", "commit_outcome_unknown", "Cần đối chiếu sản phẩm đã lưu trước khi chạy lại."));
                stop = true;
            }
        }

        var rows = results.SelectMany(g => g.Rows).OrderBy(r => r.Row).ToArray();
        int Count(string status) => results.Count(g => g.Status == status);
        return Results.Ok(new ProductImportResult(new(results.Count, rows.Length, Count("ready"), Count("committed"),
            Count("failed"), Count("unknown"), Count("notProcessed")), results, rows));
    }

    private static ProductImportGroupResult Failed(ImportProductLine[] lines, string status, string code, string message,
        IReadOnlyDictionary<string, string[]>? fields = null)
    {
        var rows = lines.Select(l =>
        {
            var errorColumn = code == "sku_exists" ? "sku" : "product_slug";
            if (fields is not null && fields.TryGetValue("variantPosition", out var at) &&
                at.Length == 1 && int.TryParse(at[0], out var index) && index >= 0 && index < lines.Length && lines[index].Row != l.Row)
                return new ProductImportRow(l.Row, null, null, null, false, null, null, null, [],
                    [new(l.Row, "product_slug", "group_rejected", "Một dòng khác của sản phẩm có lỗi.")]);
            return new ProductImportRow(l.Row, null, null, null, false, null, null, null, [],
                [new(l.Row, errorColumn, code, message)]);
        }).ToArray();
        var slug = lines[0].Cell("product_slug");
        return new("skipGroup", status, slug.Length > 0 ? slug : null, null, lines[0].Cell("name"), [], rows);
    }

    internal static IResult FileProblem(ProductImportFileException error, HttpContext? http = null) =>
        Results.Problem(statusCode: error.Status, title: error.Message,
            type: http is null ? null : $"https://teapremium/errors/{error.Error.Code}",
            extensions: http is null
                ? new Dictionary<string, object?> { ["code"] = error.Error.Code, ["errors"] = new[] { error.Error } }
                : new Dictionary<string, object?> { ["code"] = error.Error.Code, ["errors"] = new[] { error.Error }, ["traceId"] = http.TraceIdentifier });

    internal static async Task<T> ReadUploadAsync<T>(HttpRequest request, Func<MemoryStream, string, T> parse, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = Math.Min(limit.MaxRequestBodySize ?? long.MaxValue, ProductImportFile.MaxRequest);
        if (request.ContentLength > ProductImportFile.MaxRequest)
            throw ProductImportFile.Limit(1, "file", "Yêu cầu tải tệp vượt giới hạn.");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ||
            !media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ProductImportFileException(415, 1, "file", "unsupported_format", "Chọn yêu cầu multipart/form-data.");
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70)
            throw ProductImportFile.Invalid(1, "file", "invalid_boundary", "Multipart thiếu dấu phân cách hợp lệ.");

        var reader = new MultipartReader(boundary, request.Body) { HeadersCountLimit = 8, HeadersLengthLimit = 8192 };
        var section = await reader.ReadNextSectionAsync(ct);
        if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
            !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(disposition.Name).Value != "file" ||
            HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value is not { Length: > 0 } filename)
            throw ProductImportFile.Invalid(1, "file", "invalid_upload", "Gửi đúng một trường tệp có tên file.");
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (var count = await section.Body.ReadAsync(buffer, ct); count > 0; count = await section.Body.ReadAsync(buffer, ct))
        {
            if (data.Length + count > ProductImportFile.MaxFile) throw ProductImportFile.Limit(1, "file", "Tệp vượt 5 MiB.");
            data.Write(buffer, 0, count);
        }
        if (await reader.ReadNextSectionAsync(ct) is not null)
            throw ProductImportFile.Invalid(1, "file", "invalid_upload", "Gửi đúng một trường tệp có tên file.");
        data.Position = 0;
        return parse(data, filename);
    }
}
