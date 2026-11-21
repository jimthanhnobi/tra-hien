using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentOpeningBatchesInitializeOnceRegardlessOfFileOrder(bool multiple)
    {
        var gate = new ParentGate();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var a = Assert.Single((await h.Create()).Variants);
        var b = Assert.Single((await h.Create("Second")).Variants);
        var firstFile = multiple ? Csv((b.Sku, 2, 200), (a.Sku, 1, 100)) : Csv((a.Sku, 1, 100));
        var secondFile = multiple ? Csv((a.Sku, 1, 100), (b.Sku, 2, 200)) : firstFile;
        var firstPlan = await Preview(h.App.Client, firstFile);
        var secondPlan = await Preview(h.App.Client, secondFile);
        gate.Enable();
        var first = Apply(h.App.Client, firstFile, firstPlan);
        Task<OpeningResult>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = Apply(h.App.Client, secondFile, secondPlan);
            await WaitForParentLock(h.App.RuntimeConnection);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        Assert.NotNull(second);
        var losing = await second.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(losing.Committed);
        Assert.All(losing.Rows, row => Assert.Equal("opening_already_recorded", Assert.Single(row.Errors).Code));
        await using var db = h.App.OwnerContext();
        Assert.Equal(multiple ? 2 : 1, await db.Set<StockLevel>().CountAsync());
        Assert.Equal(multiple ? 2 : 1, await db.Set<StockMovement>().CountAsync());
        Assert.Equal(multiple ? 3 : 1, await db.Set<StockMovement>().SumAsync(x => x.Quantity));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CatalogAndOpeningShareParentVariantLockProtocol(bool catalogFirst, bool skuChange)
    {
        var gate = new ParentGate();
        await using var h = await ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        var product = await h.Create();
        var variant = Assert.Single(product.Variants);
        var file = Csv((variant.Sku, 2, 100));
        var plan = await Preview(h.App.Client, file);
        var path = ProductApiTests.Root + "/" + product.Id + "/variants/" + variant.Id;
        object patch = skuChange ? new { variant.RowVersion, sku = "CHANGED-1" } : new { variant.RowVersion, isUniquePiece = true };
        Task<HttpResponseMessage> ChangeCatalog() => h.App.MutateAsync(HttpMethod.Patch, path, patch);
        Task<HttpResponseMessage> ApplyOpening() => Send(h.App.Client, file, "false", plan.PreviewToken);

        gate.Enable();
        var first = catalogFirst ? ChangeCatalog() : ApplyOpening();
        Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = catalogFirst ? ApplyOpening() : ChangeCatalog();
            await WaitForParentLock(h.App.RuntimeConnection);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        using var won = await first.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(second);
        using var lost = await second.WaitAsync(TimeSpan.FromSeconds(20));
        if (catalogFirst)
        {
            Assert.Equal(HttpStatusCode.OK, won.StatusCode);
            var failed = await ReadOpening(lost);
            Assert.False(failed.Committed);
            Assert.Equal(skuChange ? "sku_identity_changed" : "unique_piece_violation", Assert.Single(Assert.Single(failed.Rows).Errors).Code);
        }
        else
        {
            Assert.True((await ReadOpening(won)).Committed);
            await Problem(lost, HttpStatusCode.Conflict, skuChange ? "sku_locked" : "unique_piece_violation");
        }
        await using var db = h.App.OwnerContext();
        var saved = await db.Set<Variant>().SingleAsync();
        Assert.Equal(catalogFirst && !skuChange, saved.IsUniquePiece);
        Assert.Equal(catalogFirst && skuChange ? "CHANGED-1" : variant.Sku, saved.Sku);
        Assert.Equal(catalogFirst ? 0 : 2, await db.Set<StockLevel>().SumAsync(x => x.OnHand));
        Assert.Equal(catalogFirst ? 0 : 1, await db.Set<StockMovement>().CountAsync());
    }

    [Fact]
    public async Task MixedCurrentEligibilityRejectsWholePreviouslyValidFile()
    {
        await using var h = await ProductHarness.Create(fixture);
        var a = Assert.Single((await h.Create()).Variants);
        var b = Assert.Single((await h.Create("Second")).Variants);
        var both = Csv((a.Sku, 1, 1), (b.Sku, 1, 1));
        var bothPlan = await Preview(h.App.Client, both);
        var first = Csv((a.Sku, 1, 1));
        Assert.True((await Apply(h.App.Client, first, await Preview(h.App.Client, first))).Committed);

        var before = await Fingerprint(h);
        var rejected = await Apply(h.App.Client, both, bothPlan);
        Assert.False(rejected.Committed);
        Assert.Equal(1, rejected.InvalidRows);
        Assert.Equal(1, rejected.ValidRows);
        Assert.Equal("opening_already_recorded", Assert.Single(rejected.Rows[0].Errors).Code);
        Assert.Empty(rejected.Rows[1].Errors);
        Assert.Equal(before, await Fingerprint(h));
    }

    private static async Task WaitForParentLock(string connection)
    {
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var query = new NpgsqlCommand("""
SELECT EXISTS(SELECT FROM pg_stat_activity
  WHERE datname=current_database() AND wait_event_type='Lock'
    AND cardinality(pg_blocking_pids(pid))>0
    AND query LIKE '%FROM catalog.products WHERE id=%FOR NO KEY UPDATE%')
""", observer);
        while (!(bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            await Task.Delay(20, timeout.Token);
    }

    private sealed class ParentGate : DbCommandInterceptor
    {
        private int enabled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enable() => Interlocked.Exchange(ref enabled, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM catalog.products WHERE id=", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)
                && Interlocked.Exchange(ref enabled, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
