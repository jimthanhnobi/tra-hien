using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Media;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-02: real restricted tp_app login + real PG17 + real IAM/CSRF and decoder; only R2 is fake.
public sealed class MediaApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/media";
    [Theory, MemberData(nameof(MediaContentTests.Formats), MemberType = typeof(MediaContentTests))]
    public async Task AllRequiredFormatsUploadAndCompleteThroughRealApi(string extension, string mime)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var bytes = MediaContentTests.Bytes(extension);
        using var created = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body(mime.StartsWith("video/", StringComparison.Ordinal) ? "evidence" : "catalog", bytes.Length, mime));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var intent = (await created.Content.ReadFromJsonAsync<UploadIntent>())!;
        await h.StageAsync(intent.UploadId, bytes, mime);
        using var complete = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var asset = (await complete.Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal(mime, asset.MimeType); Assert.Equal(bytes.Length, asset.ByteSize);
    }
    [Fact]
    public async Task CrossUserPendingSessionAndRevokedPermissionCannotComplete()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var intent = await h.IntentAsync(); await h.StageAsync(intent.UploadId);
        var other = await h.CreateStaffAsync("content"); await h.App.LoginAsync(other, P009Harness.Password + "-Changed");
        using (var denied = await h.CompleteAsync(intent.UploadId)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var own = await h.IntentAsync(); await h.StageAsync(own.UploadId);
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Email == other);
            // Real role reassignment revokes catalog.write at the next validated cookie request.
            var identity = scope.ServiceProvider.GetRequiredService<IStaffIdentity>();
            var view = (await identity.SessionAsync(user.Id, default))!.User;
            var patched = await scope.ServiceProvider.GetRequiredService<ICommandHandler<PatchStaff, StaffView>>().Handle(
                new(user.Id, view.RowVersion, null, null, ["sales"], null, h.App.OwnerId, h.App.OwnerStamp), default);
            Assert.True(patched.IsSuccess);
        }
        // Revocation may reject the old cookie (401) before the purpose check. Re-login observes the new role.
        await h.App.LoginAsync(other, P009Harness.Password + "-Changed");
        using var revoked = await h.CompleteAsync(own.UploadId); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        await using var check = h.App.OwnerContext(); Assert.Empty(await check.Set<MediaAsset>().ToArrayAsync());
    }
    [Fact]
    public async Task ValidUploadRepeatConcurrentCompleteAndLibraryReturnOneImmutableAsset()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var intent = await h.IntentAsync();
        Assert.Equal(h.Clock.UtcNow.AddMinutes(10), intent.ExpiresAt);
        Assert.Equal("image/png", intent.Headers["Content-Type"]);
        Assert.Contains("private.fake.invalid/pending/", intent.PutUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("private-filename", intent.PutUrl, StringComparison.Ordinal);
        await h.StageAsync(intent.UploadId);
        // Two HTTP requests (global media concurrency limit = 2); independent DB contexts/transactions.
        var results = await Task.WhenAll(h.CompleteAsync(intent.UploadId), h.CompleteAsync(intent.UploadId));
        foreach (var response in results) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await results[0].Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal(intent.UploadId, first.Id);
        Assert.Equal(32, first.Width); Assert.Equal(24, first.Height); Assert.Equal(64, first.ChecksumSha256!.Length);
        Assert.Matches("^https://public\\.fake\\.invalid/catalog/fixture-pot/fixture-pot-[a-z0-9]{6}\\.png$", first.Url);
        // The original presigned key is still mutable, but completed assets never read it again.
        await h.StageAsync(intent.UploadId, new byte[] { 1, 2, 3 });
        using var repeated = await h.CompleteAsync(intent.UploadId);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(first, await repeated.Content.ReadFromJsonAsync<AssetView>());
        using var listing = await h.App.Client.GetAsync(Root + "?q=fixture-pot&purpose=catalog&page=1&pageSize=1");
        var page = (await listing.Content.ReadFromJsonAsync<MediaPage>())!;
        Assert.Single(page.Items); Assert.Equal(1, page.TotalItems);
        await using var db = h.App.OwnerContext();
        Assert.Equal(1, await db.Set<MediaAsset>().CountAsync()); Assert.Single(h.Storage.Assets);
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var response in results) response.Dispose();
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("size")]
    [InlineData("spoof")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("wrong-key")]
    public async Task CompleteRejectsMissingWrongOrMismatchedObjectWithoutCreatingAsset(string kind)
    {
        await using var h = await Harness.CreateAsync(fixture);
        var bytes = MediaContentTests.Bytes("png");
        if (kind == "truncated") bytes = bytes[..(bytes.Length / 2)];
        if (kind == "spoof") bytes = MediaContentTests.Bytes("jpg");
        var intent = await h.IntentAsync(size: kind is "spoof" or "truncated" ? bytes.Length : 127);
        if (kind == "size") bytes = [1];
        if (kind != "missing") await h.StageAsync(intent.UploadId, bytes, kind == "metadata" ? "image/jpeg" : "image/png", kind == "wrong-key");
        using var response = await h.CompleteAsync(intent.UploadId);
        Assert.Equal(kind is "missing" or "wrong-key" ? HttpStatusCode.NotFound : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var db = h.App.OwnerContext(); Assert.Empty(await db.Set<MediaAsset>().ToArrayAsync()); Assert.Empty(h.Storage.Assets);
        Assert.Equal("pending", (await db.Set<UploadSession>().SingleAsync()).Status);
    }

    [Fact]
    public async Task ExpiredIntentRejectsCompleteAndCleanupRetriesStorageFailureThenRemovesOrphans()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var intent = await h.IntentAsync(); await h.StageAsync(intent.UploadId);
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(10);
        using var expired = await h.CompleteAsync(intent.UploadId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
        await h.App.ExecuteAsync("UPDATE media.upload_sessions SET created_at=created_at-interval '25 hours'");
        await using (var db = h.App.OwnerContext())
        {
            var session = await db.Set<UploadSession>().SingleAsync();
            await h.Storage.WriteAssetAsync(session.StorageKey, session.Purpose, new(MediaContentTests.Bytes("png"), "image/png"), default);
        }
        h.Storage.Fail = true;
        await Assert.ThrowsAsync<MediaStorageException>(h.CleanupAsync);
        await using (var db = h.App.OwnerContext()) Assert.Equal("pending", (await db.Set<UploadSession>().SingleAsync()).Status);
        h.Storage.Fail = false;
        Assert.Equal(1, await h.CleanupAsync()); Assert.Empty(h.Storage.Staging); Assert.Empty(h.Storage.Assets);
        await using (var db = h.App.OwnerContext()) Assert.Equal("expired", (await db.Set<UploadSession>().SingleAsync()).Status);
        Assert.Equal(0, await h.CleanupAsync());
    }

    [Fact]
    public async Task DatabaseRollbackLeavesRecoverableObjectAndRetryCreatesOneAssetCleanupPreservesIt()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var intent = await h.IntentAsync(); await h.StageAsync(intent.UploadId);
        await h.App.ExecuteAsync("""
            CREATE FUNCTION media.reject_asset_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-filename@example.test'; END $$;
            CREATE TRIGGER reject_asset_fixture BEFORE INSERT ON media.assets FOR EACH ROW EXECUTE FUNCTION media.reject_asset_fixture();
            """);
        using var failed = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await using (var db = h.App.OwnerContext())
        {
            Assert.Empty(await db.Set<MediaAsset>().ToArrayAsync()); Assert.Equal("pending", (await db.Set<UploadSession>().SingleAsync()).Status);
        }
        Assert.Single(h.Storage.Assets); // Safe, validated orphan; no library row exposes it.
        await h.App.ExecuteAsync("DROP TRIGGER reject_asset_fixture ON media.assets; DROP FUNCTION media.reject_asset_fixture()");
        using var success = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var asset = await success.Content.ReadFromJsonAsync<AssetView>();
        await h.App.ExecuteAsync("UPDATE media.upload_sessions SET created_at=created_at-interval '25 hours'");
        Assert.Equal(1, await h.CleanupAsync()); Assert.Empty(h.Storage.Staging); Assert.Single(h.Storage.Assets);
        using var repeated = await h.CompleteAsync(intent.UploadId);
        Assert.Equal(asset, await repeated.Content.ReadFromJsonAsync<AssetView>());
        Assert.DoesNotContain(h.Logs.Lines, x => x.Contains("private-filename", StringComparison.Ordinal) || x.Contains("method=PUT", StringComparison.Ordinal) || x.Contains("method=GET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageFailureRollsBackIntentAndCompleteAndRecoverySucceeds()
    {
        await using var h = await Harness.CreateAsync(fixture);
        h.Storage.Fail = true;
        using var failedIntent = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedIntent.StatusCode);
        await using (var db = h.App.OwnerContext()) Assert.Empty(await db.Set<UploadSession>().ToArrayAsync());
        h.Storage.Fail = false;
        var intent = await h.IntentAsync(); await h.StageAsync(intent.UploadId);
        h.Storage.Fail = true;
        using var failed = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        h.Storage.Fail = false;
        using var success = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task AuthenticationCsrfOnboardingAndPurposePermissionsAreEnforced()
    {
        await using var h = await Harness.CreateAsync(fixture, login: false);
        using (var anonymous = await h.App.Client.GetAsync(Root)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var anonymous = await h.App.Client.PostAsJsonAsync(Root + "/upload-intents", Body())) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await h.App.LoginAsync();
        using (var missingCsrf = await h.App.Client.PostAsJsonAsync(Root + "/upload-intents", Body())) Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var initialEmail = await h.CreateStaffAsync("content", changePassword: false);
        await h.App.LoginAsync(initialEmail, P009Harness.Password);
        using (var onboarding = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body())) Assert.Equal(HttpStatusCode.Forbidden, onboarding.StatusCode);
        await h.App.LoginAsync();
        var sales = await h.CreateStaffAsync("sales");
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        using (var denied = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body())) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body(purpose: "content"))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var evidence = await h.IntentAsync("evidence"); await h.StageAsync(evidence.UploadId);
        using (var ok = await h.CompleteAsync(evidence.UploadId)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await h.App.LoginAsync();
        var content = await h.CreateStaffAsync("content");
        await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        foreach (var purpose in new[] { "catalog", "content", "branding" })
        {
            var intent = await h.IntentAsync(purpose); await h.StageAsync(intent.UploadId);
            using var ok = await h.CompleteAsync(intent.UploadId); Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using (var denied = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body(purpose: "evidence"))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await h.App.Client.GetAsync(Root + "?purpose=evidence")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task PrivateEvidenceDoesNotLeakThroughLibraryOrCrossUserCompleteEvenWithSamePermission()
    {
        await using var h = await Harness.CreateAsync(fixture);
        var intent = await h.IntentAsync("evidence", slug: "customer-name-must-not-be-used");
        Assert.DoesNotContain("customer-name", intent.PutUrl, StringComparison.Ordinal);
        await h.StageAsync(intent.UploadId);
        using var completed = await h.CompleteAsync(intent.UploadId);
        var asset = (await completed.Content.ReadFromJsonAsync<AssetView>())!;
        Assert.StartsWith("https://private.fake.invalid/", asset.Url, StringComparison.Ordinal);
        Assert.Equal(h.Clock.UtcNow.AddMinutes(10), asset.UrlExpiresAt);
        var sales = await h.CreateStaffAsync("sales"); await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        using (var crossUser = await h.CompleteAsync(intent.UploadId)) Assert.Equal(HttpStatusCode.Forbidden, crossUser.StatusCode);
        var page = await h.App.Client.GetFromJsonAsync<MediaPage>(Root + "?purpose=evidence"); Assert.Empty(page!.Items);
        await h.App.LoginAsync();
        var content = await h.CreateStaffAsync("content"); await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        page = await h.App.Client.GetFromJsonAsync<MediaPage>(Root); Assert.Empty(page!.Items);
        Assert.DoesNotContain(h.Logs.Lines, x => x.Contains("private-filename", StringComparison.Ordinal) || x.Contains("private.fake.invalid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("image/png", 15728641, "catalog", "fixture-pot")]
    [InlineData("video/mp4", 1, "catalog", "fixture-pot")]
    [InlineData("video/quicktime", 52428801, "evidence", "fixture-pot")]
    [InlineData("text/html", 127, "content", "fixture-pot")]
    [InlineData("image/png", 127, "unknown", "fixture-pot")]
    [InlineData("image/png", 127, "catalog", "../customer@example.test")]
    public async Task InvalidIntentReturns400AndWritesNothing(string mime, long size, string purpose, string slug)
    {
        await using var h = await Harness.CreateAsync(fixture);
        using var response = await h.App.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body(purpose, size, mime, slug));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = h.App.OwnerContext(); Assert.Empty(await db.Set<UploadSession>().ToArrayAsync());
    }
    [Fact]
    public async Task OpenApiDocumentsExactlyTheThreeMediaEndpointsAndTypedContracts()
    {
        await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(builder, "CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient(); using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        Assert.Equal(3, paths.EnumerateObject().Count(x => x.Name.StartsWith(Root, StringComparison.Ordinal)));
        Assert.True(paths.GetProperty(Root + "/upload-intents").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty(Root + "/upload-intents/{id}/complete").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty(Root).TryGetProperty("get", out _));
    }
    private static object Body(string purpose = "catalog", long size = 127, string mime = "image/png", string? slug = "fixture-pot")
        => new { fileName = "private-filename@example.test.png", mimeType = mime, byteSize = size, purpose, slugHint = slug };

    private sealed class MutableClock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }
    private sealed class Harness(P009Harness app, FakeMediaStorage storage, MutableClock clock, Logs logs) : IAsyncDisposable
    {
        public P009Harness App => app;
        public FakeMediaStorage Storage => storage;
        public MutableClock Clock => clock;
        public Logs Logs => logs;
        public static async Task<Harness> CreateAsync(PlatformDatabaseFixture fixture, bool login = true)
        {
            var storage = new FakeMediaStorage(); var clock = new MutableClock(); var logs = new Logs();
            var app = await P009Harness.CreateAsync(fixture, services =>
            {
                services.RemoveAll<IMediaStorage>(); services.AddSingleton<IMediaStorage>(storage);
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock);
                services.AddSingleton<ILoggerProvider>(logs);
                services.RemoveAll<MediaStorageOptions>(); services.AddSingleton(new MediaStorageOptions { FfmpegPath = MediaContentTests.Ffmpeg, FfprobePath = MediaContentTests.Ffprobe });
            });
            if (login) await app.LoginAsync();
            return new(app, storage, clock, logs);
        }
        public async Task<UploadIntent> IntentAsync(string purpose = "catalog", long size = 127, string? slug = "fixture-pot")
        {
            using var response = await app.MutateAsync(HttpMethod.Post, Root + "/upload-intents", Body(purpose, size, slug: slug));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<UploadIntent>())!;
        }
        public async Task StageAsync(Guid id, byte[]? bytes = null, string mime = "image/png", bool wrongKey = false)
        {
            await using var db = app.OwnerContext(); var session = await db.Set<UploadSession>().SingleAsync(x => x.Id == id);
            storage.Staging[MediaKeys.Staging(session) + (wrongKey ? "-wrong" : "")] = new(bytes ?? MediaContentTests.Bytes("png"), mime);
        }
        public Task<HttpResponseMessage> CompleteAsync(Guid id) => app.MutateAsync(HttpMethod.Post, Root + $"/upload-intents/{id}/complete", new { });
        public async Task<int> CleanupAsync()
        {
            await using var scope = app.Factory.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<UploadCleanup>().RunAsync(default);
        }
        public async Task<string> CreateStaffAsync(string role, bool changePassword = true)
        {
            var email = role + "-" + Guid.NewGuid().ToString("N") + "@example.test";
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>().Handle(new(email, "Fixture", P009Harness.Password, [role], app.OwnerId, app.OwnerStamp), default);
            Assert.True(created.IsSuccess, created.IsSuccess ? "" : created.Error.Code);
            if (changePassword) Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
                .Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
            return email;
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
