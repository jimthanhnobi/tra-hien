using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// F5 / BR-SEO-22/03: actual HTTP writers, real PG17 locks, both transaction orders.
public sealed class ProductArchiveConcurrencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    public static TheoryData<string, bool, bool> Orders
    {
        get
        {
            var data = new TheoryData<string, bool, bool>();
            foreach (var kind in new[] { "product", "category", "collection", "material", "artisan" })
                foreach (var archiveFirst in new[] { true, false })
                    foreach (var slugChange in new[] { true, false }) data.Add(kind, archiveFirst, slugChange);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task DestinationStateIsProtectedBeforeTheGraphAndUntilArchiveCommit(string kind, bool archiveFirst, bool slugChange)
    {
        var gate = new ArchiveGate { ArchiveFirst = archiveFirst };
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        Guid destination;
        if (kind == "product")
        {
            var target = await ProductFinalBlockerTests.Published(h); target = await h.Patch(target, new { slug = "target" }); destination = target.Id;
        }
        else destination = await ProductFinalBlockerTests.Destination(h, kind, kind is "category" or "collection" ? "visible" : "published");
        var p = await ProductFinalBlockerTests.Published(h);
        var table = Table(kind); var targetPath = ProductFinalBlockerTests.Prefix(kind) + "target";
        var version = kind == "product" ? (await h.Get(destination)).RowVersion
            : (await h.App.Client.GetFromJsonAsync<CatalogView>("/api/v1/admin/" + table + "/" + destination))!.RowVersion;
        Task<HttpResponseMessage> Archive() => h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/archive", new { p.RowVersion, redirectTo = targetPath });
        Task<HttpResponseMessage> DestinationWrite() => kind == "product" && !slugChange
            ? h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + destination + "/archive", new { rowVersion = version, redirectTo = "/danh-muc/p105-tea" })
            : h.App.MutateAsync(HttpMethod.Patch, "/api/v1/admin/" + table + "/" + destination,
                slugChange ? new { rowVersion = version, slug = "target-renamed" } : (object)new { rowVersion = version, status = kind is "category" or "collection" ? "hidden" : "archived" });
        gate.Source = p.Id; gate.Destination = destination; gate.Enabled = true;
        var first = archiveFirst ? Archive() : DestinationWrite(); Task<HttpResponseMessage>? second = null;
        var nowait = !archiveFirst && kind is "category" or "collection" or "artisan";
        try
        {
            await gate.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = archiveFirst ? DestinationWrite() : Archive();
            if (nowait)
            {
                await gate.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var committedBefore = await ProductFinalBlockerTests.Fingerprint(h); gate.SecondRelease.TrySetResult();
                using var conflict = await second.WaitAsync(TimeSpan.FromSeconds(20));
                await ProductG4FindingTests.Problem(conflict, HttpStatusCode.Conflict, "concurrency_conflict");
                Assert.Equal(committedBefore, await ProductFinalBlockerTests.Fingerprint(h));
            }
            else await WaitForBlockedWriter(h.App.RuntimeConnection);
        }
        finally { gate.FirstRelease.TrySetResult(); }
        Assert.NotNull(second);
        if (nowait)
        {
            using var changed = await first.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            // Once the destination writer commits, a fresh request must resolve the new graph/state.
            var afterDestination = await ProductFinalBlockerTests.Fingerprint(h);
            await VerifyArchiveResult(h, p, await Archive(), slugChange || kind == "product");
            if (!slugChange) Assert.Equal(afterDestination, await ProductFinalBlockerTests.Fingerprint(h));
            return;
        }
        string committed;
        try
        {
            await gate.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
            committed = await ProductFinalBlockerTests.Fingerprint(h);
            if (archiveFirst)
            {
                var saved = await h.Get(p.Id); Assert.Equal("archived", saved.Status); Assert.Equal(targetPath, saved.ArchivedRedirectPath);
                // Second writer is paused BEFORE changing status/slug: this is the archive commit state.
                await using var observer = new NpgsqlConnection(h.App.RuntimeConnection); await observer.OpenAsync();
                await using var query = new NpgsqlCommand("SELECT status FROM catalog." + table + " WHERE id=@id", observer); query.Parameters.AddWithValue("id", destination);
                Assert.Equal(kind == "product" ? "active" : kind is "category" or "collection" ? "visible" : "published", await query.ExecuteScalarAsync());
            }
        }
        finally { gate.SecondRelease.TrySetResult(); }
        using var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(20));
        if (archiveFirst) Assert.Equal(HttpStatusCode.OK, secondResult.StatusCode);
        else
        {
            var succeeds = slugChange || kind == "product";
            if (!succeeds) { await ProductG4FindingTests.Problem(secondResult, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(committed, await ProductFinalBlockerTests.Fingerprint(h)); }
            else await VerifyArchiveResult(h, p, secondResult, true);
        }
        // Later target lifecycle after a successful archive is a separate Store/P3 contract.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedDiscoveryReturnsConflictWithoutLockingANewAggregateAfterTheGraph(bool beforeProtection)
    {
        var gate = new ArchiveGate { BeforeGraph = !beforeProtection, BeforeProtection = beforeProtection };
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var target = await ProductFinalBlockerTests.Destination(h, "category", "visible");
        var other = await ProductFinalBlockerTests.Destination(h, "category", "visible", "other");
        var p = await ProductFinalBlockerTests.Published(h);
        await h.App.ExecuteAsync("INSERT INTO content.redirects(source_path,target_path) VALUES ('/danh-muc/old','/danh-muc/target')");
        var changing = beforeProtection ? target : other;
        var category = (await h.App.Client.GetFromJsonAsync<CatalogView>("/api/v1/admin/categories/" + changing))!;
        gate.Enabled = true;
        var archive = h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root + "/" + p.Id + "/archive", new { p.RowVersion, redirectTo = "/danh-muc/old" });
        string committed;
        try
        {
            await gate.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var reclaim = await h.App.MutateAsync(HttpMethod.Patch, "/api/v1/admin/categories/" + changing,
                new { category.RowVersion, slug = beforeProtection ? "target-renamed" : "old" }).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode); committed = await ProductFinalBlockerTests.Fingerprint(h);
        }
        finally { gate.FirstRelease.TrySetResult(); }
        using var result = await archive.WaitAsync(TimeSpan.FromSeconds(20));
        await ProductG4FindingTests.Problem(result, HttpStatusCode.Conflict, "concurrency_conflict"); Assert.Equal(committed, await ProductFinalBlockerTests.Fingerprint(h));
    }

    private static async Task VerifyArchiveResult(ProductHarness h, ProductData p, HttpResponseMessage response, bool succeeds)
    {
        using (response)
        {
            if (!succeeds) { await ProductG4FindingTests.Problem(response, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion); return; }
            var saved = await ProductHarness.Read(response, HttpStatusCode.OK); Assert.Equal("archived", saved.Status);
            // A renamed destination resolves to its new slug. An archived Product resolves to its public fallback.
            Assert.True(saved.ArchivedRedirectPath == "/danh-muc/p105-tea" || saved.ArchivedRedirectPath!.EndsWith("/target-renamed", StringComparison.Ordinal));
        }
    }
    private static string Table(string kind) => kind switch { "product" => "products", "category" => "categories", "collection" => "collections", "material" => "materials", _ => "artisans" };
    private static async Task WaitForBlockedWriter(string connection)
    {
        await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid<>pg_backend_pid() AND datname=current_database() AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0)", observer);
            if ((bool)(await query.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(20, deadline.Token);
        }
    }
    private sealed class ArchiveGate : DbCommandInterceptor
    {
        public bool ArchiveFirst { get; init; }
        public bool BeforeGraph { get; init; }
        public bool BeforeProtection { get; init; }
        public Guid Source { get; set; }
        public Guid Destination { get; set; }
        public volatile bool Enabled;
        private int first, second;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task PauseFirst(CancellationToken ct)
        { if (Interlocked.CompareExchange(ref first, 1, 0) == 0) { FirstEntered.TrySetResult(); await FirstRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); } }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Enabled && BeforeGraph && command.CommandText.Contains("pg_advisory_xact_lock(8104, 100)", StringComparison.Ordinal)) await PauseFirst(cancellationToken); return result; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (Enabled && ArchiveFirst && command.CommandText.Contains("pg_advisory_xact_lock(8104, 100)", StringComparison.Ordinal)) await PauseFirst(cancellationToken); return result; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && BeforeProtection && command.CommandText.Contains("FROM content.redirects", StringComparison.Ordinal)) await PauseFirst(cancellationToken);
            if (!Enabled || BeforeGraph || !command.CommandText.Contains("FOR ", StringComparison.Ordinal) || command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal)) return result;
            var id = command.Parameters.Cast<DbParameter>().Select(x => x.Value).OfType<Guid>().FirstOrDefault();
            if (!ArchiveFirst && id == Destination) await PauseFirst(cancellationToken);
            if (id == (ArchiveFirst ? Destination : Source) && Interlocked.CompareExchange(ref second, 1, 0) == 0)
            { SecondEntered.TrySetResult(); await SecondRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            return result;
        }
    }
}
