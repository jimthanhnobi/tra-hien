using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// F1 / BR-CAT-04.6: force both parent-lock acquisition orders and inspect every failed mutation.
public sealed class ProductG4ConcurrencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Theory]
    [InlineData("publish-first")]
    [InlineData("patch-first")]
    [InlineData("activation")]
    [InlineData("creation")]
    public async Task FinalAxisInvariantIsValidatedUnderTheParentLockBeforeCommit(string scenario)
    {
        var gate = new ParentLocks();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var p = await ProductPublicationTests.Ready(h, await h.Create(), 150);
        p = await h.Change(p, "/variants", new { priceVnd = 2000, status = scenario == "activation" ? "inactive" : "active", attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) } });
        if (scenario is "activation" or "creation") p = await h.Change(p, "/publish", new { });
        if (scenario == "activation") p = await h.Change(p, "/variants", new { priceVnd = 3000, status = "inactive", attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) } });
        var second = p.Variants[1]; var third = p.Variants.Last();
        // For creation, start with one active180; each concurrent request tries to add active245.
        if (scenario == "creation") p = await h.Change(p, "/variants/" + second.Id, new { status = "inactive" }, HttpMethod.Patch, second.RowVersion);
        var originalParentVersion = p.RowVersion;
        Task<HttpResponseMessage> Publish(string version) => h.App.MutateAsync(HttpMethod.Post, Path(p.Id) + "/publish", new { rowVersion = version });
        Task<HttpResponseMessage> PatchAxis() => h.App.MutateAsync(HttpMethod.Patch, Path(p.Id) + "/variants/" + second.Id,
            new { second.RowVersion, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 180) } });
        Task<HttpResponseMessage> Activate(VariantData v) => h.App.MutateAsync(HttpMethod.Patch, Path(p.Id) + "/variants/" + v.Id, new { v.RowVersion, status = "active" });
        Task<HttpResponseMessage> Create(string version) => h.App.MutateAsync(HttpMethod.Post, Path(p.Id) + "/variants",
            new { rowVersion = version, priceVnd = 3000, attributes = new[] { new ProductValue(h.VariantAttribute, ValueNumber: 245) } });
        Task<HttpResponseMessage> First() => scenario switch { "publish-first" => Publish(originalParentVersion), "patch-first" => PatchAxis(), "activation" => Activate(second), _ => Create(originalParentVersion) };
        Task<HttpResponseMessage> Second() => scenario switch { "publish-first" => PatchAxis(), "patch-first" => Publish(originalParentVersion), "activation" => Activate(third), _ => Create(originalParentVersion) };
        gate.Enabled = true; var first = First(); Task<HttpResponseMessage>? competing = null;
        try
        {
            await gate.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(20)); competing = Second();
            await WaitForBlockedParent(h.App.RuntimeConnection); Assert.False(competing.IsCompleted);
        }
        finally { gate.FirstRelease.TrySetResult(); }
        string committed;
        try
        {
            await gate.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
            committed = await ProductG4FindingTests.Fingerprint(h);
        }
        finally { gate.SecondRelease.TrySetResult(); }
        Assert.NotNull(competing); using var result = await competing.WaitAsync(TimeSpan.FromSeconds(20));
        var stale = scenario is "patch-first" or "creation";
        await ProductG4FindingTests.Problem(result, stale ? HttpStatusCode.Conflict : HttpStatusCode.UnprocessableEntity, stale ? "concurrency_conflict" : "variant_requirements_not_met");
        Assert.Equal(committed, await ProductG4FindingTests.Fingerprint(h));
        if (stale)
        {
            // Fresh token distinguishes legitimate stale-parent rejection from final-state validation.
            var saved = await h.Get(p.Id); using var retry = scenario == "patch-first" ? await Publish(saved.RowVersion) : await Create(saved.RowVersion);
            await ProductG4FindingTests.Problem(retry, HttpStatusCode.UnprocessableEntity, scenario == "patch-first" ? "publish_requirements_not_met" : "variant_requirements_not_met");
            Assert.Contains("variant_axis_duplicate", await retry.Content.ReadAsStringAsync()); Assert.Equal(committed, await ProductG4FindingTests.Fingerprint(h));
        }
        var final = await h.Get(p.Id);
        if (scenario == "patch-first") Assert.Equal("draft", final.Status);
        else
        {
            Assert.Equal("active", final.Status);
            var active = final.Variants.Where(v => v.Status == "active").ToArray();
            Assert.Equal(active.Length, active.Select(v => v.Attributes.Single(x => x.AttributeId == h.VariantAttribute).ValueNumber).Distinct().Count());
        }
    }
    private static string Path(Guid id) => ProductApiTests.Root + "/" + id;
    private static async Task WaitForBlockedParent(string connection)
    {
        // Same tp_app role can observe its own sessions; tp_owner cannot inspect tp_app activity.
        // No pg_read_all_stats/superuser grant is added for test instrumentation.
        await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0 AND query LIKE '%FROM catalog.products WHERE id=%FOR NO KEY UPDATE%')", observer);
            if ((bool)(await query.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(20, deadline.Token);
        }
    }
    private sealed class ParentLocks : DbCommandInterceptor
    {
        public volatile bool Enabled;
        private int count;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM catalog.products WHERE id=", StringComparison.Ordinal) && command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal))
            {
                var acquired = Interlocked.Increment(ref count);
                if (acquired == 1) { FirstEntered.TrySetResult(); await FirstRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
                if (acquired == 2) { SecondEntered.TrySetResult(); await SecondRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            }
            return result;
        }
    }
}
