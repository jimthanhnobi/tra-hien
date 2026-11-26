using Microsoft.OpenApi;
using TeaPremium.Api.Catalog;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Import;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Crm;

internal static partial class CustomerImportEndpoint
{
    [LoggerMessage(EventId = 41, Level = LogLevel.Information,
        Message = "Customer import completed. DryRun={DryRun} Committed={Committed} TotalRows={TotalRows} Imported={ImportedCount} Skipped={SkippedCount} Errors={ErrorCount} Trace={TraceId}.")]
    private static partial void ImportCompleted(
        ILogger logger,
        bool dryRun,
        bool committed,
        int totalRows,
        int importedCount,
        int skippedCount,
        int errorCount,
        string traceId);

    public static void MapCustomerImport(this RouteGroupBuilder customers)
    {
        customers.MapPost("/import", ImportAsync)
            .RequireAuthorization("customers.write")
            .WithMetadata(new RequireCsrfHeader())
            .WithDescription("Preview (dryRun=true) or apply (dryRun=false) legacy customer import from CSV UTF-8 or XLSX (up to 2,000 rows, 5 MiB).")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Parameters!.Single(x => x.Name == "dryRun") is OpenApiParameter mode)
                {
                    mode.Required = true;
                    mode.Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean };
                }

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
            .Produces<CustomerImportResult>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(409)
            .ProducesProblem(413)
            .ProducesProblem(415);
    }

    private static async Task<IResult> ImportAsync(
        HttpContext http,
        string? dryRun,
        IQueryHandler<PreviewCustomerImport, CustomerImportResult> preview,
        ICommandHandler<ApplyCustomerImport, CustomerImportResult> apply,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (http.Request.Query["dryRun"].Count != 1 || dryRun is not ("true" or "false"))
        {
            return ProductImportEndpoint.FileProblem(
                ProductImportFile.Invalid(1, "dryRun", "invalid_mode", "Chọn dryRun=true hoặc dryRun=false."),
                http);
        }

        CustomerImportLine[] lines;
        try
        {
            lines = await ProductImportEndpoint.ReadUploadAsync(
                http.Request,
                (data, filename) => TabularImportFile
                    .Read(data, filename, CustomerImportSchema.Table)
                    .Select(CustomerImportSchema.FromTabular)
                    .ToArray(),
                ct);
        }
        catch (TabularImportFileException error)
        {
            return ProductImportEndpoint.FileProblem(
                new ProductImportFileException(error.Status, error.Row, error.Column, error.Code, error.Message),
                http);
        }
        catch (ProductImportFileException error)
        {
            return ProductImportEndpoint.FileProblem(error, http);
        }
        catch (InvalidDataException)
        {
            return ProductImportEndpoint.FileProblem(
                ProductImportFile.Invalid(1, "file", "invalid_multipart", "Yêu cầu multipart không hợp lệ."),
                http);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return ProductImportEndpoint.FileProblem(
                ProductImportFile.Limit(1, "file", "Tệp vượt 5 MiB."),
                http);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            return ProductImportEndpoint.FileProblem(
                ProductImportFile.Invalid(1, "file", "invalid_multipart", "Yêu cầu tải tệp bị ngắt hoặc không hợp lệ."),
                http);
        }

        var actor = CustomerEndpoints.Actor(http);
        var isDryRun = dryRun == "true";
        var result = isDryRun
            ? await preview.Handle(new PreviewCustomerImport(lines, actor), ct)
            : await apply.Handle(new ApplyCustomerImport(lines, actor), ct);

        if (!result.IsSuccess)
        {
            return result.Error.ToProblem(http);
        }

        var logger = loggerFactory.CreateLogger("CustomerImport");
        if (logger.IsEnabled(LogLevel.Information))
        {
            var summary = result.Value.Summary;
            ImportCompleted(
                logger,
                summary.DryRun,
                summary.Committed,
                summary.TotalRows,
                summary.ImportedCount,
                summary.SkippedCount,
                summary.ErrorCount,
                http.TraceIdentifier);
        }

        return Results.Ok(result.Value);
    }
}
