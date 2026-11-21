using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Pricing;
using TeaPremium.Domain.Pricing;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.SalePriceApiTests;

namespace TeaPremium.IntegrationTests;

// BR-PRC-02/03: exercise both real PostgreSQL lock acquisition orders.
public sealed class SalePriceConcurrencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreationAndVariantUpdatesSerializeAgainstTheSameCommittedState(bool statusWrite, bool pricingFirst)
    {
        var gate = new ParentGate();
        await using var h = await CreateHarness(gate);
        var p = await PricedProduct(h, price: 1000);
        var initial = Assert.Single(p.Variants);
        var route = Path(initial.Id);
        var variantRoute = $"{ProductApiTests.Root}/{p.Id}/variants/{initial.Id}";
        object patch = statusWrite ? new { initial.RowVersion, status = "inactive" } : new { initial.RowVersion, priceVnd = 400 };
        Task<HttpResponseMessage> Add() => h.App.MutateAsync(HttpMethod.Post, route, Body(initial));
        Task<HttpResponseMessage> Patch() => h.App.MutateAsync(HttpMethod.Patch, variantRoute, patch);
        var (first, second) = await Race(h, gate, pricingFirst ? Add : Patch, pricingFirst ? Patch : Add);
        using var firstResponse = first;
        using var secondResponse = second;
        var current = Assert.Single((await h.Get(p.Id)).Variants);
        Assert.NotEqual(initial.RowVersion, current.RowVersion);
        var list = (await h.App.Client.GetFromJsonAsync<SalePriceList>(route))!;
        if (pricingFirst)
        {
            var scheduled = await ReadCreated(first);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(scheduled, Assert.Single(list.Items));
            var effective = PricingCalculator.Calculate(current.Status == "active", current.PriceVnd,
                [new(scheduled.SalePriceVnd, scheduled.StartsAt, scheduled.EndsAt)], Start);
            if (statusWrite) Assert.Null(effective);
            else Assert.Equal(new EffectivePrice(400, 400), effective);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            await Problem(second, HttpStatusCode.Conflict, "concurrency_conflict");
            Assert.Empty(list.Items);
            var before = await Fingerprint(h);
            using var retry = await h.App.MutateAsync(HttpMethod.Post, route, Body(current));
            await Problem(retry, statusWrite ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.BadRequest,
                statusWrite ? "variant_not_sellable" : "validation_failed");
            Assert.Equal(before, await Fingerprint(h));
        }
    }

    [Fact]
    public async Task ConcurrentOverlappingSubmissionsCommitExactlyOneMutation()
    {
        var gate = new ParentGate();
        await using var h = await CreateHarness(gate);
        var variant = Assert.Single((await PricedProduct(h)).Variants);
        var route = Path(variant.Id);
        var (first, second) = await Race(h, gate,
            () => h.App.MutateAsync(HttpMethod.Post, route, Body(variant)),
            () => h.App.MutateAsync(HttpMethod.Post, route, Body(variant, 1, 3)));
        using var firstResponse = first;
        using var secondResponse = second;
        var row = await ReadCreated(first);
        await Problem(second, HttpStatusCode.Conflict, "sale_price_overlap");
        Assert.Equal(row, Assert.Single((await h.App.Client.GetFromJsonAsync<SalePriceList>(route))!.Items));
        await using var db = h.App.OwnerContext();
        Assert.Single(await db.AuditLogs.Where(x => x.EntityType == "pricing.sale_prices").ToArrayAsync());
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDeleteAndRelatedWriteNeverRemoveOrInvalidateTheWrongRow(bool replace)
    {
        var gate = new ParentGate();
        await using var h = await CreateHarness(gate);
        var variant = Assert.Single((await PricedProduct(h)).Variants);
        var route = Path(variant.Id);
        using var created = await h.App.MutateAsync(HttpMethod.Post, route, Body(variant));
        var row = await ReadCreated(created);
        Task<HttpResponseMessage> Delete() => h.App.MutateAsync(HttpMethod.Delete, route + "/" + row.Id, new { row.RowVersion });
        Task<HttpResponseMessage> Replace() => h.App.MutateAsync(HttpMethod.Post, route, Body(variant));
        var (first, second) = await Race(h, gate, Delete, replace ? Replace : Delete);
        using var firstResponse = first;
        using var secondResponse = second;
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var list = (await h.App.Client.GetFromJsonAsync<SalePriceList>(route))!;
        if (replace)
        {
            var replacement = await ReadCreated(second);
            Assert.NotEqual(row.Id, replacement.Id);
            Assert.Equal(replacement, Assert.Single(list.Items));
        }
        else
        {
            await Problem(second, HttpStatusCode.NotFound, "not_found");
            Assert.Empty(list.Items);
        }
        await using var db = h.App.OwnerContext();
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityType == "pricing.sale_prices" && x.EntityId == row.Id && x.Action == "delete"));
        Assert.Equal(replace ? 4 : 3, await db.OutboxMessages.CountAsync());
    }

    private Task<ProductHarness> CreateHarness(ParentGate gate) =>
        ProductHarness.Create(fixture, services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));

    private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> Race(ProductHarness h, ParentGate gate,
        Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        gate.Enabled = true;
        var running = first();
        Task<HttpResponseMessage> competing;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            competing = second();
            await WaitForBlockedParent(h.App.RuntimeConnection);
            Assert.False(competing.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        await Task.WhenAll(running, competing).WaitAsync(TimeSpan.FromSeconds(25));
        return (await running, await competing);
    }

    private static async Task WaitForBlockedParent(string connection)
    {
        await using var observer = new NpgsqlConnection(connection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await observer.OpenAsync(deadline.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database()
                  AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0
                  AND query LIKE '%FROM catalog.products WHERE id=%FOR NO KEY UPDATE%')
                """, observer);
            if ((bool)(await query.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    private sealed class ParentGate : DbCommandInterceptor
    {
        private int acquisitions;
        public volatile bool Enabled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM catalog.products WHERE id=", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)
                && Interlocked.Increment(ref acquisitions) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
