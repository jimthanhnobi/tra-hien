using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;

namespace TeaPremium.IntegrationTests;

public sealed class OrderConcurrencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly OrderContactSnapshot SampleContact = new(
        "Synthetic Buyer", "+84900000101", "buyer@example.invalid",
        "Synthetic Receiver", "+84900000102", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "10 Duong Thu Nghiem");
    private static readonly OrderAttributionSnapshot SampleAttribution = new(OrderAcquisitionSource.Direct, true);

    private sealed class SequenceCodeGenerator(params string[] codes) : IOrderCodeGenerator
    {
        private int _index;
        public string NextCode(DateOnly vietnamDate)
        {
            var idx = Interlocked.Increment(ref _index) - 1;
            return idx < codes.Length ? codes[idx] : OrderCode.GenerateRandom(vietnamDate);
        }
    }

    [Fact]
    public async Task AcP201TwentyConcurrentOrdersOnLastUniquePieceExactlyOneSucceedsAndNineteenReturnOutOfStock()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, sku) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 3_500_000L);

        const int concurrency = 20;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
            if (Interlocked.Increment(ref readyCount) == concurrency)
                startGate.TrySetResult();
            await startGate.Task;

            return await handler.Handle(new CreateWebOrder(
                customerId,
                null,
                OrderPaymentMethod.Cod,
                SampleContact,
                [new(variantId, 1)],
                null,
                null,
                null,
                SampleAttribution,
                null), CancellationToken.None);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        var successes = results.Where(r => r.IsSuccess).ToArray();
        var failures = results.Where(r => !r.IsSuccess).ToArray();

        Assert.Single(successes);
        Assert.Equal(19, failures.Length);
        Assert.All(failures, f =>
        {
            Assert.Equal("out_of_stock", f.Error.Code);
            var detail = Assert.Single(OutOfStockErrorFormatting.Parse(f.Error));
            Assert.Equal(variantId, detail.VariantId);
            Assert.Equal(sku, detail.Sku);
            Assert.Equal(0, detail.Available);
        });

        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.orders"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.reservations WHERE status='active'"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.stock_movements WHERE movement_type='sale'"));
    }

    [Fact]
    public async Task ReverseMultiVariantOrdersZeroDeadlocksAndExactReservedTotals()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantA, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: false, onHand: 50, priceVnd: 400_000L, suffix: "a");
        var variantB = await AddProductAndVariantAsync(app, isUniquePiece: false, onHand: 50, priceVnd: 600_000L, suffix: "b");

        const int pairs = 10;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, pairs * 2).Select(i => Task.Run(async () =>
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
            OrderLineRequest[] lines = i % 2 == 0
                ? [new(variantA, 1), new(variantB, 2)]
                : [new(variantB, 2), new(variantA, 1)];
            if (Interlocked.Increment(ref readyCount) == pairs * 2)
                startGate.TrySetResult();
            await startGate.Task;

            return await handler.Handle(new CreateWebOrder(
                customerId, null, OrderPaymentMethod.Cod, SampleContact, lines,
                null, null, null, SampleAttribution, null), CancellationToken.None);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsSuccess ? "" : $"{r.Error.Code}: {r.Error.Message}"));
        Assert.Equal(20, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantA}'"));
        Assert.Equal(40, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantB}'"));
    }

    [Fact]
    public async Task CatalogAndOrderShareLockOrderWithoutDeadlock()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, productId, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: false, onHand: 30, priceVnd: 500_000L);

        const int workers = 10;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            if (Interlocked.Increment(ref readyCount) == workers)
                startGate.TrySetResult();
            await startGate.Task;

            try
            {
                if (i % 2 == 0)
                {
                    var orderHandler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
                    var res = await orderHandler.Handle(new CreateWebOrder(
                        customerId, null, OrderPaymentMethod.Cod, SampleContact, [new(variantId, 1)],
                        null, null, null, SampleAttribution, null), CancellationToken.None);
                    Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
                }
                else
                {
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var productStore = scope.ServiceProvider.GetRequiredService<IProductStore>();
                    var res = await uow.ExecuteAsync(async ct =>
                    {
                        var loaded = await productStore.LoadAsync(productId, update: true, ct);
                        Assert.NotNull(loaded);
                        return Result.Success(true);
                    }, CancellationToken.None);
                    Assert.True(res.IsSuccess);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Empty(errors);
        Assert.Equal(5, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
    }

    [Fact]
    public async Task OrderCodeCollisionRetriesViaSavepointWithoutAbortingOrLeakingFailedAudit()
    {
        const string collidingCode = "TP261123COLL";
        const string freshCode = "TP261123OK23";
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IOrderCodeGenerator>();
            services.AddSingleton<IOrderCodeGenerator>(new SequenceCodeGenerator(collidingCode, collidingCode, freshCode));
        });

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: false, onHand: 5, priceVnd: 1_000_000L);

        // Pre-seed an existing order occupying collidingCode.
        await app.ExecuteAsync($"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at)
VALUES ('{Guid.CreateVersion7()}','{collidingCode}','admin','phone','{customerId}','pending','cod',
 'Existing','+84900000199','Existing','+84900000199','79','TP Ho Chi Minh','79001','Phuong 1','1 St',
 1000000,0,0,1000000,'direct',true,now());
""");

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
        var result = await handler.Handle(new CreateWebOrder(
            customerId, null, OrderPaymentMethod.Cod, SampleContact, [new(variantId, 1)],
            null, null, null, SampleAttribution, null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        Assert.Equal(freshCode, result.Value.Code);

        // Exactly 1 audit log row for the newly created order (failed savepoint attempts were rolled back & detached).
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.audit_logs WHERE entity_type='sales.orders' AND entity_id='{result.Value.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.orders WHERE code='{freshCode}'"));
    }

    [Fact]
    public async Task ConcurrentSameCustomerOrdersWithPerCustomerLimitOneVoucherExactlyOneSucceedsWithoutDeadlock()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: false, onHand: 30, priceVnd: 500_000L, suffix: "vc");
        var voucherId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,usage_limit,per_customer_limit,status)
VALUES ('{voucherId}','ONCEPERCUST','manual','fixed',100000,200000,now() - interval '1 day',now() + interval '10 day',10,1,'active');
""");

        const int concurrency = 12;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
            if (Interlocked.Increment(ref readyCount) == concurrency)
                startGate.TrySetResult();
            await startGate.Task;

            return await handler.Handle(new CreateWebOrder(
                customerId,
                null,
                OrderPaymentMethod.Cod,
                SampleContact,
                [new(variantId, 1)],
                "ONCEPERCUST",
                null,
                null,
                SampleAttribution,
                null), CancellationToken.None);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        var successes = results.Where(r => r.IsSuccess).ToArray();
        var failures = results.Where(r => !r.IsSuccess).ToArray();

        var winner = Assert.Single(successes);
        Assert.Equal(voucherId, winner.Value.VoucherId);
        Assert.Equal(concurrency - 1, failures.Length);
        Assert.All(failures, f => Assert.Equal("voucher_customer_limit", f.Error.Code));

        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.orders"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{voucherId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM pricing.voucher_redemptions WHERE voucher_id='{voucherId}' AND status='applied'"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
    }

    private static int _skuSeq = 2000;

    internal static async Task<(Guid CustomerId, Guid ProductId, Guid VariantId, string Sku)> SeedProductVariantAndCustomerAsync(
        P009Harness app, bool isUniquePiece, int onHand, long priceVnd, string suffix = "01", long? avgCostVnd = 400_000L)
    {
        var customerId = Guid.CreateVersion7();
        var productTypeId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var seq = Interlocked.Increment(ref _skuSeq);
        var sku = $"AM-XX-{seq:D4}";

        await app.ExecuteAsync($"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP Ho Chi Minh') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;
INSERT INTO crm.customers(id,code,full_name,phone_e164,origin)
VALUES ('{customerId}','KH-P2-{suffix}','Synthetic Customer','+84900000101','web') ON CONFLICT DO NOTHING;
INSERT INTO catalog.product_types(id,code,name)
VALUES ('{productTypeId}','tea_p2_{suffix}','Teapot P2') ON CONFLICT DO NOTHING;
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
VALUES ('{productId}','{productTypeId}','{sku}','pot-p2-{suffix}','Am P2 {suffix}','active',now(),200,'am p2 {suffix}');
INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,is_unique_piece,low_stock_threshold)
VALUES ('{variantId}','{productId}','{sku}','active',{priceVnd},{(isUniquePiece ? "true" : "false")},1);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd)
VALUES ('{variantId}',{onHand},0,{(avgCostVnd.HasValue ? avgCostVnd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")});
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,ref_type,actor_type,occurred_at)
VALUES ('{Guid.CreateVersion7()}','{variantId}','opening',{onHand},{(avgCostVnd ?? 0L)},{onHand},'import','system',now());
""");
        return (customerId, productId, variantId, sku);
    }

    internal static async Task<Guid> AddProductAndVariantAsync(
        P009Harness app, bool isUniquePiece, int onHand, long priceVnd, string suffix, long? avgCostVnd = 300_000L)
    {
        var productTypeId = await ScalarAsync<Guid>(app, "SELECT id FROM catalog.product_types LIMIT 1");
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var seq = Interlocked.Increment(ref _skuSeq);
        var sku = $"AM-XX-{seq:D4}";

        var movementSql = onHand > 0
            ? $"INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,ref_type,actor_type,occurred_at) VALUES ('{Guid.CreateVersion7()}','{variantId}','opening',{onHand},{(avgCostVnd ?? 0L)},{onHand},'import','system',now());"
            : "";

        await app.ExecuteAsync($"""
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
VALUES ('{productId}','{productTypeId}','{sku}','pot-p2-{suffix}','Am P2 {suffix}','active',now(),200,'am p2 {suffix}');
INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,is_unique_piece,low_stock_threshold)
VALUES ('{variantId}','{productId}','{sku}','active',{priceVnd},{(isUniquePiece ? "true" : "false")},1);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd)
VALUES ('{variantId}',{onHand},0,{(avgCostVnd.HasValue ? avgCostVnd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")});
{movementSql}
""");
        return variantId;
    }

    internal static async Task<T> ScalarAsync<T>(P009Harness app, string sql)
    {
        await using var cn = new NpgsqlConnection(app.OwnerConnection);
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
