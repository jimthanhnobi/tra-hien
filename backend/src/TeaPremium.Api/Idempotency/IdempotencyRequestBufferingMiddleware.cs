using TeaPremium.Api.Middleware;
using TeaPremium.Application.Results;

namespace TeaPremium.Api.Idempotency;

/// <summary>After routing and before binding. Unmarked endpoints retain their usual body handling.</summary>
public sealed class IdempotencyRequestBufferingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotencyEndpoint>() is not null)
        {
            context.Request.EnableBuffering(bufferThreshold: IdempotencyEndpoint.MaxRequestBytes + 1);
            var buffer = new byte[8192];
            var total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, IdempotencyEndpoint.MaxRequestBytes + 1 - total)), context.RequestAborted)) > 0)
            {
                total += read;
                if (total > IdempotencyEndpoint.MaxRequestBytes)
                {
                    await new Error("validation_failed", "Request vượt giới hạn cho phép.", ErrorType.Validation)
                        .ToProblem(context).ExecuteAsync(context);
                    return;
                }
            }
            context.Request.Body.Position = 0;
        }
        await next(context);
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>Register security filters before this filter so they run on every replay.</summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder, IdempotencyEndpoint policy)
        => builder.WithMetadata(policy).AddEndpointFilter(policy);
}
