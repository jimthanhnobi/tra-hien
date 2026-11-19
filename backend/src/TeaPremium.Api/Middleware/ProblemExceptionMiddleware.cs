using TeaPremium.Application.Results;
using TeaPremium.Domain;
using System.Text.Json;

namespace TeaPremium.Api.Middleware;

public sealed class ProblemExceptionMiddleware(RequestDelegate next, ILogger<ProblemExceptionMiddleware> logger)
{
    // The JSON field name is specified as correlation_id, so keep it in snake_case.
#pragma warning disable CA1727
    private static readonly Action<ILogger, string, Exception?> DomainRejected =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "DomainRejected"),
            "Domain rule rejected request; correlation_id {correlation_id}");
    private static readonly Action<ILogger, string, Exception?> UnhandledFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, "UnhandledFailure"),
            "Unhandled request failure; correlation_id {correlation_id}");
#pragma warning restore CA1727

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (TeaPremium.Application.Media.MediaStorageException)
        {
            await TeaPremium.Api.Iam.AdminSecurity.Problem(context, 503, "media_storage_unavailable").ExecuteAsync(context);
        }
        catch (DomainException exception)
        {
            DomainRejected(logger, context.TraceIdentifier, null);
            await Error.BusinessRule(exception.Code, DomainException.PublicMessage).ToProblem(context).ExecuteAsync(context);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Never log exception messages, request bodies, headers or paths: they may contain PII.
            UnhandledFailure(logger, context.TraceIdentifier, null);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://teapremium/errors/internal_error",
                title = "Lỗi hệ thống.",
                status = 500,
                code = "internal_error",
                traceId = context.TraceIdentifier
            }));
        }
    }
}
