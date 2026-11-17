using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Api.Idempotency;

/// <summary>
/// Attach after all endpoint security filters. ActorScope must identify the authenticated
/// scheme/issuer/subject (or a verified guest identity); never take it from a client header.
/// ResponseIsSafe must allowlist the public response schema, including string values.
/// Version Operation when changing response or request semantics. No direct HTTP writes,
/// independent transactions/contexts, external effects, or concurrent commands are supported.
/// </summary>
public sealed record IdempotencyEndpoint(
    string Operation,
    Func<HttpContext, string?> ActorScope,
    Func<JsonElement, bool> ResponseIsSafe) : IEndpointFilter
{
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var keys = http.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !Guid.TryParseExact(keys[0], "D", out var key))
            return InvalidKey().ToProblem(http);
        var actor = ActorScope(http);
        if (string.IsNullOrWhiteSpace(actor))
            return new Error("forbidden", "Không có định danh hợp lệ.", ErrorType.Forbidden).ToProblem(http);
        if (string.IsNullOrWhiteSpace(Operation) || Operation.Length > 200)
            throw new InvalidOperationException("Idempotency operation must be a stable versioned identifier.");

        // Framed JSON avoids ambiguous concatenation. Store only digests, never actor PII.
        var scope = Digest(JsonSerializer.SerializeToUtf8Bytes(new[] { "idem-v1", Operation, actor }));
        var body = await ReadRequestAsync(http.Request, ct);
        if (body is null)
            return new Error("validation_failed", "Request vượt giới hạn cho phép.", ErrorType.Validation).ToProblem(http);
        var hash = Digest(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "idem-v1", scope, http.Request.Method, http.Request.PathBase.Value, http.Request.Path.Value,
            http.Request.QueryString.Value, http.Request.ContentType, Digest(body)
        }));
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var uow = http.RequestServices.GetRequiredService<EfUnitOfWork>();
        var clock = http.RequestServices.GetRequiredService<IClock>();
        object? response = null;
        var result = await uow.ExecuteRequestAsync(async token =>
        {
            // A transaction lock works across connections/instances and disappears on rollback,
            // cancellation or connection loss. Hash collisions only serialize unrelated keys.
            var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(
                Encoding.UTF8.GetBytes("TeaPremium.Idempotency:" + scope + key.ToString("D"))));
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
            var now = clock.UtcNow.UtcDateTime;
            var cutoff = now - Retention;
            await db.IdempotencyKeys.Where(x => x.Scope == scope && x.Key == key && x.CreatedAt < cutoff)
                .ExecuteDeleteAsync(token);
            var stored = await db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == scope && x.Key == key, token);
            if (stored is not null)
            {
                if (stored.RequestHash != hash)
                    return Result.Failure<int>(new Error("idempotency_key_reused", "Khóa đã dùng cho request khác.", ErrorType.Conflict));
                response = ReadStored(stored);
                return Result.Success(0);
            }

            var reservation = new IdempotencyKey { Scope = scope, Key = key, RequestHash = hash, CreatedAt = now };
            db.IdempotencyKeys.Add(reservation);
            await db.SaveChangesAsync(token);
            // Successful results are data, never arbitrary IResult execution inside a transaction.
            // A rejecting stream also catches accidental direct writes before commit.
            var originalBody = http.Features.Get<IHttpResponseBodyFeature>()
                ?? throw new InvalidOperationException("HTTP response feature is missing.");
            var headers = http.Response.Headers.ToDictionary(x => x.Key, x => x.Value);
            try
            {
                http.Features.Set<IHttpResponseBodyFeature>(new RejectResponseFeature());
                response = await next(context);
                if (http.Response.HasStarted || http.Response.StatusCode != 200
                    || headers.Count != http.Response.Headers.Count
                    || headers.Any(x => !http.Response.Headers.TryGetValue(x.Key, out var value) || value != x.Value))
                    throw new InvalidOperationException("Idempotent endpoints must return response data without HTTP mutations.");
            }
            catch
            {
                http.Response.Headers.Clear();
                foreach (var header in headers) http.Response.Headers[header.Key] = header.Value;
                http.Response.StatusCode = 200;
                throw;
            }
            finally { http.Features.Set(originalBody); }
            if (response is not IdempotentResponse success)
            {
                if (response is IStatusCodeHttpResult { StatusCode: >= 400 })
                    return Result.Failure<int>(new Error("validation_failed", "Request thất bại.", ErrorType.Validation));
                throw new InvalidOperationException("Idempotent endpoints require an explicitly safe JSON response.");
            }
            var bytes = await SerializeBoundedAsync(success.Body, token);
            ValidateResponse(success.StatusCode, bytes);
            reservation.ResponseStatus = success.StatusCode;
            // jsonb normalizes JSON. A versioned base64 envelope preserves original wire bytes.
            reservation.ResponseBody = JsonSerializer.Serialize(new StoredResponse(1, Convert.ToBase64String(bytes)), JsonOptions);
            reservation.CreatedAt = clock.UtcNow.UtcDateTime;
            response = new CommittedResponse(success.StatusCode, bytes);
            return Result.Success(0);
        }, ct);
        return result.IsSuccess ? response : response is IStatusCodeHttpResult { StatusCode: >= 400 }
            ? response : result.Error.ToProblem(http);
    }

    private CommittedResponse ReadStored(IdempotencyKey stored)
    {
        if (stored.ResponseBody is null || stored.ResponseBody.Length > MaxResponseBytes * 2)
            throw new InvalidOperationException("Stored idempotency response is incomplete or invalid.");
        var envelope = JsonSerializer.Deserialize<StoredResponse>(stored.ResponseBody, JsonOptions);
        if (envelope is not { Version: 1 } || envelope.BodyBase64 is null)
            throw new InvalidOperationException("Stored idempotency response version is unsupported.");
        var bytes = Convert.FromBase64String(envelope.BodyBase64);
        var status = stored.ResponseStatus ?? 0;
        ValidateResponse(status, bytes);
        return new CommittedResponse(status, bytes);
    }

    private void ValidateResponse(int status, byte[] bytes)
    {
        if (status is < 200 or >= 300 or 204 or 205 || bytes.Length > MaxResponseBytes)
            throw new InvalidOperationException("Idempotency response is unsupported or exceeds its limit.");
        using var json = JsonDocument.Parse(bytes);
        if (!ResponseIsSafe(json.RootElement))
            throw new InvalidOperationException("Response is not allowed by the endpoint public data policy.");
    }

    private static async Task<byte[]> SerializeBoundedAsync(object body, CancellationToken ct)
    {
        await using var stream = new BoundedMemoryStream(MaxResponseBytes);
        await JsonSerializer.SerializeAsync(stream, body, body.GetType(), JsonOptions, ct);
        return stream.ToArray();
    }

    private static async Task<byte[]?> ReadRequestAsync(HttpRequest request, CancellationToken ct)
    {
        // Endpoint filters run after binding. EnableBuffering must run before binding (Program).
        if (!request.Body.CanSeek) throw new InvalidOperationException("Request buffering must precede endpoint binding.");
        request.Body.Position = 0;
        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxRequestBytes) return null;
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        finally { request.Body.Position = 0; }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Error InvalidKey() => new("validation_failed", "Idempotency-Key phải là UUID hợp lệ.", ErrorType.Validation);
    private sealed record StoredResponse(int Version, string BodyBase64);
    private sealed record CommittedResponse(int StatusCode, byte[] Bytes) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCode;
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.ContentLength = Bytes.Length;
            await httpContext.Response.Body.WriteAsync(Bytes, httpContext.RequestAborted);
        }
    }

    private sealed class BoundedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
        private void Check(int count)
        {
            if (Length + count > limit) throw new InvalidOperationException("Idempotency response exceeds its limit.");
        }
    }

    private sealed class RejectWritesStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw Reject();
        public override void Write(ReadOnlySpan<byte> buffer) => throw Reject();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Reject();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Reject();
        private static InvalidOperationException Reject() => new("Direct response writes are forbidden before commit.");
    }

    private sealed class RejectResponseFeature : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = new RejectWritesStream();
        public PipeWriter Writer => throw Reject();
        public void DisableBuffering() => throw Reject();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw Reject();
        public Task CompleteAsync() => throw Reject();
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => throw Reject();
        private static InvalidOperationException Reject() => new("Direct HTTP response operations are forbidden before commit.");
    }
}

/// <summary>Return only an allowlisted public DTO; the endpoint filter validates it before commit.</summary>
public sealed record IdempotentResponse(int StatusCode, object Body);
