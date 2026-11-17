using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeaPremium.Api.Idempotency;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class IdempotencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task T01T02FirstAndReplayHaveIdenticalCommittedBytesAndAtomicAuditOutbox()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var first = await Send(client, key);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        using var replay = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal(bytes, await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json", replay.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, probe.Executions);
        await AssertCommitted(probe, key, 1);
    }

    [Fact]
    public async Task T03ChangedRequestIdentityConflictsWithoutExecution()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var first = await Send(client, key);
        using var changed = await Send(client, key, "{\"value\":2}");
        await AssertProblem(changed, HttpStatusCode.Conflict, "idempotency_key_reused");
        using var query = await Send(client, key, path: "/__idem/work?changed=true");
        await AssertProblem(query, HttpStatusCode.Conflict, "idempotency_key_reused");
        using var route = await Send(client, key, path: "/__idem/alias");
        await AssertProblem(route, HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal(1, probe.Executions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad-key")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00000000-0000-0000-0000-000000000000,00000000-0000-0000-0000-000000000001")]
    public async Task T04RequiredUuidRejected(string? key)
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        using var request = Request(key);
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, probe.Executions);
    }

    [Fact]
    public async Task T05OperationsAndAuthenticatedActorsAreIsolated()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var one = await Send(client, key);
        using var two = await Send(client, key, path: "/__idem/other");
        using var three = await Send(client, key, actor: "bob");
        using var replay = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, two.StatusCode);
        Assert.Equal(HttpStatusCode.Created, three.StatusCode);
        Assert.Equal(await one.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(3, probe.Executions);
        await AssertCommitted(probe, key, 3);
    }

    [Fact]
    public async Task T06TwoHostsTwentyConcurrentIdenticalRequestsHaveOneSideEffect()
    {
        var probe = new Probe { Pause = true };
        using var factory1 = Factory(probe);
        using var factory2 = Factory(probe);
        using var client1 = factory1.CreateClient();
        using var client2 = factory2.CreateClient();
        var key = Guid.NewGuid();
        var first = Send(client1, key);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var others = Enumerable.Range(0, 19).Select(i => Send(i % 2 == 0 ? client1 : client2, key)).ToArray();
        // Ensure at least one independent backend is actually waiting on the PostgreSQL lock.
        await WaitForLockWaiter();
        Assert.Equal(1, probe.Executions);
        probe.Release.TrySetResult();
        var responses = await Task.WhenAll(others.Prepend(first));
        try
        {
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
            Assert.Single(bodies.Distinct(StringComparer.Ordinal));
            Assert.Equal(1, probe.Executions);
            await AssertCommitted(probe, key, 1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task T07ConcurrentConflictingRequestsNeverBothExecute()
    {
        var probe = new Probe { Pause = true };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var first = Send(client, key);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var conflict = Send(client, key, "{\"value\":2}");
        await WaitForLockWaiter();
        probe.Release.TrySetResult();
        using var success = await first;
        using var rejected = await conflict;
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        await AssertProblem(rejected, HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal(1, probe.Executions);
        await AssertCommitted(probe, key, 1);
    }

    [Theory]
    [InlineData("failure", HttpStatusCode.UnprocessableEntity)]
    [InlineData("exception", HttpStatusCode.InternalServerError)]
    [InlineData("swallowed", HttpStatusCode.InternalServerError)]
    public async Task T08T09FailureAfterSaveDoesNotPoisonRetry(string failure, HttpStatusCode status)
    {
        var probe = new Probe { FailFirst = failure };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var failed = await Send(client, key);
        Assert.Equal(status, failed.StatusCode);
        await AssertCommitted(probe, key, 0);
        using var retry = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(2, probe.Executions);
        await AssertCommitted(probe, key, 1);
    }

    [Fact]
    public async Task T09RequestCancellationAfterSaveRollsBackAndCanRetry()
    {
        var probe = new Probe { Pause = true };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        using var request = Request(key.ToString("D"));
        var pending = client.SendAsync(request, cts.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // The retry waits for the original transaction's rollback through the same DB lock.
        probe.Pause = false;
        using var retry = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        await AssertCommitted(probe, key, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T10CommitFailureOrLostAcknowledgementIsSafeForClientRetry(bool afterCommit)
    {
        var probe = new Probe { CommitFailure = afterCommit ? "after" : "before" };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var failed = await Send(client, key);
        await AssertProblem(failed, HttpStatusCode.InternalServerError, "internal_error");
        await AssertCommitted(probe, key, afterCommit ? 1 : 0);
        using var retry = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(afterCommit ? 1 : 2, probe.Executions);
        await AssertCommitted(probe, key, 1);
    }

    [Fact]
    public async Task T10LostPostgresSessionCannotCommitAndRetryUsesFreshConnection()
    {
        var probe = new Probe { CommitFailure = "disconnect" };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var failed = await Send(client, key);
        await AssertProblem(failed, HttpStatusCode.InternalServerError, "internal_error");
        await AssertCommitted(probe, key, 0);
        using var retry = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(2, probe.Executions);
        await AssertCommitted(probe, key, 1);
    }

    [Fact]
    public async Task T11Exact48HoursReplaysThenExpiresEvenWithoutCleanup()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var first = await Send(client, key);
        probe.Clock.Advance(TimeSpan.FromHours(48));
        using var atBoundary = await Send(client, key);
        Assert.Equal(1, probe.Executions);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await atBoundary.Content.ReadAsStringAsync());
        probe.Clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, expired.StatusCode);
        Assert.Equal(2, probe.Executions);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.IdempotencyKeys.CountAsync(x => x.Key == key));
    }

    [Fact]
    public async Task T11HourlyPeriodicCleanupKeepsBoundaryAndRecentRows()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var old = Guid.NewGuid();
        var boundary = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        using var first = await Send(client, old);
        probe.Clock.Advance(TimeSpan.FromSeconds(1));
        using var second = await Send(client, boundary);
        probe.Clock.Advance(TimeSpan.FromHours(48));
        using var third = await Send(client, fresh);
        var job = ActivatorUtilities.CreateInstance<PurgeIdempotencyKeys>(factory.Services);
        Assert.True(await job.RunOnceAsync());
        await using var db = fixture.CreateContext();
        Assert.False(await db.IdempotencyKeys.AnyAsync(x => x.Key == old));
        Assert.True(await db.IdempotencyKeys.AnyAsync(x => x.Key == boundary));
        Assert.True(await db.IdempotencyKeys.AnyAsync(x => x.Key == fresh));
    }

    [Fact]
    public async Task T11StaleCleanupCannotDeleteFreshReplacement()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var first = await Send(client, key);
        probe.Clock.Advance(TimeSpan.FromHours(49));
        await using var cleanup = fixture.CreateContext();
        var expired = await cleanup.IdempotencyKeys.SingleAsync(x => x.Key == key);
        using var replacement = await Send(client, key);
        cleanup.IdempotencyKeys.Remove(expired);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cleanup.SaveChangesAsync());
        using var replay = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await replacement.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, probe.Executions);
    }

    [Fact]
    public async Task IntegrityAssertionsDetectDeliberateDuplicateBusinessExecution()
    {
        var probe = new Probe { Duplicate = true };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var response = await Send(client, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, probe.Executions);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => AssertCommitted(probe, key, 1));
    }

    [Fact]
    public async Task T12FailureAndReplayLogsNeverContainBodyKeyTokenOrActor()
    {
        var probe = new Probe { FailFirst = "exception" };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        const string body = "{\"value\":1,\"notes\":\"private-person@example.com\"}";
        using var failed = await Send(client, key, body);
        using var success = await Send(client, key, body);
        using var replay = await Send(client, key, body);
        var logs = string.Join('\n', probe.Logs.Messages);
        Assert.Contains("Unhandled request failure", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-person@example.com", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(key.ToString("D"), logs, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret-token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-actor", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T13RealAuthorizationAndAntiforgeryRunBeforeEveryReplay()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var anonymous = await Send(client, key, actor: "");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var forbidden = await Send(client, key, actor: "denied");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var noCsrf = await Send(client, key, path: "/__idem/secure");
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/__idem/csrf");
        csrfRequest.Headers.Add("X-Test-Actor", "private-actor");
        using var csrfResponse = await client.SendAsync(csrfRequest);
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfReply>();
        using var validRequest = Request(key.ToString("D"), path: "/__idem/secure");
        validRequest.Headers.Add("X-CSRF-TOKEN", csrf!.Token);
        using var success = await client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        using var deniedReplay = await Send(client, key, actor: "denied", path: "/__idem/secure");
        Assert.Equal(HttpStatusCode.Forbidden, deniedReplay.StatusCode);
        using var invalidCsrfReplay = await Send(client, key, path: "/__idem/secure");
        Assert.Equal(HttpStatusCode.Forbidden, invalidCsrfReplay.StatusCode);
        using var replayRequest = Request(key.ToString("D"), path: "/__idem/secure");
        replayRequest.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        using var replay = await client.SendAsync(replayRequest);
        Assert.Equal(await success.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, probe.Executions);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("large")]
    [InlineData("arbitrary")]
    [InlineData("headers")]
    [InlineData("write")]
    [InlineData("start")]
    public async Task UnsafeOrUnboundedResponsesRollback(string mode)
    {
        var probe = new Probe { ResponseMode = mode };
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        using var failed = await Send(client, key);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        await AssertCommitted(probe, key, 0);
    }

    [Fact]
    public async Task OversizedRequestRejectedBeforeBindingAndExecution()
    {
        var probe = new Probe();
        using var factory = Factory(probe);
        using var client = factory.CreateClient();
        using var rejected = await Send(client, Guid.NewGuid(), JsonSerializer.Serialize(new { notes = new string('x', 65536) }));
        await AssertProblem(rejected, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, probe.Executions);
    }

    [Fact]
    public async Task UnmarkedEndpointDoesNotRequireKeyOrPublishDemoInProduction()
    {
        using var factory = Factory(new Probe());
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/__idem/plain", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var production = new WebApplicationFactory<Program>();
        using var productionClient = production.CreateClient();
        using var absent = await productionClient.PostAsync("/__idem/work", null);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    private WebApplicationFactory<Program> Factory(Probe probe) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton(probe);
                services.AddSingleton<IClock>(probe.Clock);
                services.AddSingleton<ILoggerProvider>(probe.Logs);
                services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new CommitFault(probe, fixture.ConnectionString)));
                services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("allowed", policy => policy.RequireAuthenticatedUser().RequireClaim("allowed", "yes")));
                services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
                services.AddSingleton<IStartupFilter, TestEndpoints>();
            });
        });

    private async Task WaitForLockWaiter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE wait_event = 'advisory'"), CultureInfo.InvariantCulture) == 0)
            await Task.Delay(20, timeout.Token);
    }

    private async Task AssertCommitted(Probe probe, Guid key, int count)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(count, await db.IdempotencyKeys.CountAsync(x => x.Key == key));
        Assert.Equal(count, await db.Settings.CountAsync(x => x.Key.StartsWith(probe.Prefix)));
        Assert.Equal(count, await db.AuditLogs.CountAsync(x => x.EntityKey != null && x.EntityKey.StartsWith(probe.Prefix)));
        Assert.Equal(count, await db.OutboxMessages.CountAsync(x => x.Type == probe.Prefix));
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, Guid key, string body = "{\"value\":1}",
        string path = "/__idem/work", string actor = "private-actor")
    {
        using var request = Request(key.ToString("D"), body, path, actor);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(string? key, string body = "{\"value\":1}",
        string path = "/__idem/work", string actor = "private-actor")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        request.Headers.Add("X-Test-Actor", actor);
        request.Headers.Add("Authorization", "Bearer test-secret-token");
        return request;
    }

    private sealed class TestEndpoints : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/__idem", branch =>
            {
                branch.UseRouting();
                branch.UseMiddleware<CorrelationIdMiddleware>();
                branch.UseMiddleware<ProblemExceptionMiddleware>();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseMiddleware<IdempotencyRequestBufferingMiddleware>();
                branch.UseEndpoints(endpoints =>
                {
                    Map(endpoints, "/work", "work:v1");
                    Map(endpoints, "/alias", "work:v1");
                    Map(endpoints, "/other", "other:v1");
                    Map(endpoints, "/secure", "secure:v1", csrf: true);
                    endpoints.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
                        new CsrfReply(antiforgery.GetAndStoreTokens(http).RequestToken!));
                    endpoints.MapPost("/plain", () => Results.NoContent());
                });
            });
            next(app);
        };

        private static void Map(IEndpointRouteBuilder endpoints, string path, string operation, bool csrf = false)
        {
            var endpoint = endpoints.MapPost(path, async (Input input, HttpContext http, AppDbContext db,
                IUnitOfWork uow, IOutboxWriter outbox, Probe probe, CancellationToken ct) =>
            {
                var handler = new TransactionCommandDecorator<TestCommand, Guid>(new TestHandler(db, outbox, probe), uow);
                var result = await handler.Handle(new TestCommand(input.Value), ct);
                if (probe.Duplicate) await handler.Handle(new TestCommand(input.Value), ct);
                if (!result.IsSuccess && probe.FailFirst != "swallowed") return (object)result.Error.ToProblem(http);
                if (probe.ResponseMode == "arbitrary") return Results.Ok(new { token = "secret" });
                if (probe.ResponseMode == "headers") http.Response.Headers.Append("Set-Cookie", "secret=cookie");
                if (probe.ResponseMode == "write") await http.Response.WriteAsync("secret", ct);
                if (probe.ResponseMode == "start") await http.Response.StartAsync(ct);
                return probe.ResponseMode switch
                {
                    "secret" => new IdempotentResponse(201, new { token = "secret" }),
                    "large" => new IdempotentResponse(201, new { id = new string('x', 65537) }),
                    _ => new IdempotentResponse(201, new { id = result.IsSuccess ? result.Value : Guid.Empty })
                };
            }).RequireAuthorization("allowed");
            if (csrf)
                endpoint.AddEndpointFilter(async (context, next) =>
                {
                    try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
                    catch (AntiforgeryValidationException)
                    {
                        return new Error("forbidden", "CSRF không hợp lệ.", ErrorType.Forbidden).ToProblem(context.HttpContext);
                    }
                    return await next(context);
                });
            endpoint.WithIdempotency(new IdempotencyEndpoint(operation,
                http => http.User.Identity?.IsAuthenticated == true
                    ? "test:issuer:" + http.User.FindFirstValue(ClaimTypes.NameIdentifier) : null,
                json => json.ValueKind == JsonValueKind.Object && json.EnumerateObject().Count() == 1
                    && json.TryGetProperty("id", out var id) && id.TryGetGuid(out _)));
        }
    }

    private sealed record Input(int Value);
    private sealed record CsrfReply(string Token);
    private sealed record TestCommand(int Value) : ICommand<Guid>;
    private sealed class TestHandler(AppDbContext db, IOutboxWriter outbox, Probe probe) : ICommandHandler<TestCommand, Guid>
    {
        public async Task<Result<Guid>> Handle(TestCommand command, CancellationToken cancellationToken)
        {
            var execution = Interlocked.Increment(ref probe.Executions);
            var id = Guid.NewGuid();
            db.Settings.Add(new Setting { Key = probe.Prefix + id.ToString("N"), Value = "{}" });
            outbox.Enqueue(probe.Prefix, JsonSerializer.Serialize(new { id }));
            await db.SaveChangesAsync(cancellationToken);
            probe.Entered.TrySetResult();
            if (probe.Pause) await probe.Release.Task.WaitAsync(cancellationToken);
            if (execution == 1)
            {
                if (probe.FailFirst == "exception") throw new InvalidOperationException("private-person@example.com");
                if (probe.FailFirst is "failure" or "swallowed")
                    return Result.Failure<Guid>(Error.BusinessRule("test_failed", "Thất bại."));
            }
            return Result.Success(id);
        }
    }

    private sealed class Probe
    {
        public string Prefix { get; } = "idem." + Guid.NewGuid().ToString("N") + ".";
        public int Executions;
        public bool Pause { get; set; }
        public bool Duplicate { get; init; }
        public string? FailFirst { get; init; }
        public string? ResponseMode { get; init; }
        public string? CommitFailure { get; init; }
        public int CommitFaultUsed;
        public TestClock Clock { get; } = new();
        public LogCapture Logs { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class CommitFault(Probe probe, string connectionString) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (probe.CommitFailure == "before" && Interlocked.Exchange(ref probe.CommitFaultUsed, 1) == 0)
                throw new InvalidOperationException("injected pre-commit failure");
            if (probe.CommitFailure == "disconnect" && Interlocked.Exchange(ref probe.CommitFaultUsed, 1) == 0)
            {
                var pid = ((Npgsql.NpgsqlConnection)transaction.Connection!).ProcessID;
                await using var killer = new Npgsql.NpgsqlConnection(connectionString);
                await killer.OpenAsync(cancellationToken);
                await using var command = new Npgsql.NpgsqlCommand("SELECT pg_terminate_backend(@pid)", killer);
                command.Parameters.AddWithValue("pid", pid);
                Assert.Equal(true, await command.ExecuteScalarAsync(cancellationToken));
            }
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (probe.CommitFailure == "after" && Interlocked.Exchange(ref probe.CommitFaultUsed, 1) == 0)
                throw new InvalidOperationException("injected lost commit acknowledgement");
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor),
                new Claim("allowed", actor == "denied" ? "no" : "yes")], "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
}
