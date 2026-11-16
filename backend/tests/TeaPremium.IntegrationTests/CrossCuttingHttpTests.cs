using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Results;
using TeaPremium.Domain;
using TeaPremium.Infrastructure;

namespace TeaPremium.IntegrationTests;

[CollectionDefinition("JsonConsoleCapture", DisableParallelization = true)]
public sealed class JsonConsoleCaptureGroup;

[Collection("JsonConsoleCapture")]
public sealed class CrossCuttingHttpTests
{
    private static readonly IServiceProvider ProblemServices = new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    [Fact]
    public async Task ApiPropagatesSafeCorrelationId()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        const string id = "019a9b3e-624f-7e1b-9a10-2468ace02468";
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(id, response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task UnsafeCorrelationIdIsReplaced()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "0901234567");

        using var response = await client.SendAsync(request);

        Assert.NotEqual("0901234567", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
        Assert.Equal(32, response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Length);
    }

    [Fact]
    public async Task TooLongCorrelationIdIsReplaced()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, new string('a', 65));

        using var response = await client.SendAsync(request);

        Assert.Equal(32, response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().Length);
    }

    [Fact]
    public async Task CorrelationLogContainsIdWithoutLoggingRequestPii()
    {
        var context = NewContext();
        const string id = "019a9b3e624f7e1b9a102468ace02468";
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = id;
        context.Request.Path = "/api/0901234567";
        context.Request.QueryString = new QueryString("?email=secret@example.com");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("secret@example.com"));
        var logger = new CapturingLogger<CorrelationIdMiddleware>();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(id, context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
        Assert.Contains(id, string.Join(" ", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("0901234567", string.Join(" ", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", string.Join(" ", logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonConsoleOutputOmitsFrameworkRequestPathScopeAndKeepsCorrelationId()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/0901234567?email=secret@example.com");
            const string id = "019a9b3e624f7e1b9a102468ace02468";
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(SpinWait.SpinUntil(() => writer.ToString().Contains("\"correlation_id\"", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)), "JSON request log was not written to stdout.");

            var output = writer.ToString();
            Assert.DoesNotContain("0901234567", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret@example.com", output, StringComparison.Ordinal);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Last(value => value.Contains("TeaPremium.Api.Middleware.CorrelationIdMiddleware", StringComparison.Ordinal));
            using var entry = JsonDocument.Parse(line);
            Assert.False(entry.RootElement.TryGetProperty("Scopes", out _));
            Assert.Equal(id, entry.RootElement.GetProperty("State").GetProperty("correlation_id").GetString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void ClockUsesInjectedTimeProvider()
    {
        var instant = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var clock = new SystemClock(new FixedTimeProvider(instant));

        Assert.Equal(instant, clock.UtcNow);
    }

    [Theory]
    [InlineData(ErrorType.Validation, "validation_failed", 400)]
    [InlineData(ErrorType.NotFound, "not_found", 404)]
    [InlineData(ErrorType.Conflict, "concurrency_conflict", 409)]
    [InlineData(ErrorType.Forbidden, "forbidden", 403)]
    [InlineData(ErrorType.BusinessRule, "out_of_stock", 409)]
    [InlineData(ErrorType.BusinessRule, "publish_requirements_not_met", 422)]
    public async Task ProblemDetailsMapsCategoryAndCode(ErrorType type, string code, int expectedStatus)
    {
        var context = NewContext();
        var error = new Error(code, "Lỗi kiểm tra.", type);

        await error.ToProblem(context).ExecuteAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        using var body = await ReadBody(context);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("correlation-test", body.RootElement.GetProperty("traceId").GetString());
        Assert.Equal($"https://teapremium/errors/{code}", body.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ValidationProblemIncludesFieldErrors()
    {
        var context = NewContext();
        var error = new Error("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
            new Dictionary<string, string[]> { ["quantity"] = ["Phải dương."] });

        await error.ToProblem(context).ExecuteAsync(context);

        using var body = await ReadBody(context);
        Assert.Equal("Phải dương.", body.RootElement.GetProperty("errors").GetProperty("quantity")[0].GetString());
    }

    [Fact]
    public async Task DomainExceptionIsMappedWithoutLoggingItsMessage()
    {
        var context = NewContext();
        var logger = new CapturingLogger<ProblemExceptionMiddleware>();
        var middleware = new ProblemExceptionMiddleware(_ => throw new DomainException("rule_failed", "phone=0901234567"), logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.DoesNotContain("0901234567", string.Join(" ", logger.Messages), StringComparison.Ordinal);
        using var body = await ReadBody(context);
        Assert.DoesNotContain("0901234567", body.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExceptionDoesNotLeakPiiInLogOrResponse()
    {
        var context = NewContext();
        context.Request.Path = "/api/phone/0901234567";
        context.Request.QueryString = new QueryString("?email=secret@example.com");
        var logger = new CapturingLogger<ProblemExceptionMiddleware>();
        var middleware = new ProblemExceptionMiddleware(_ => throw new InvalidOperationException("secret@example.com 0901234567"), logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.DoesNotContain("0901234567", string.Join(" ", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", string.Join(" ", logger.Messages), StringComparison.Ordinal);
        using var body = await ReadBody(context);
        Assert.Equal("internal_error", body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("0901234567", body.RootElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", body.RootElement.ToString(), StringComparison.Ordinal);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "correlation-test";
        context.RequestServices = ProblemServices;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();

            public void Dispose() { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
