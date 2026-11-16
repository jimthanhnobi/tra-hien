namespace TeaPremium.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    // The JSON field name is specified as correlation_id, so keep it in snake_case.
#pragma warning disable CA1727
    private static readonly Action<ILogger, int, string, Exception?> RequestCompleted =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(1, "RequestCompleted"),
            "Request completed with status {StatusCode}; correlation_id {correlation_id}");
#pragma warning restore CA1727

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName];
        var id = supplied.Count == 1 && IsOpaqueId(supplied[0])
            ? supplied[0]!
            : Guid.CreateVersion7().ToString("N");

        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;
        await next(context);
        RequestCompleted(logger, context.Response.StatusCode, id, null);
    }

    private static bool IsOpaqueId(string? value) => value is not null &&
        (Guid.TryParseExact(value, "N", out _) || Guid.TryParseExact(value, "D", out _));
}
