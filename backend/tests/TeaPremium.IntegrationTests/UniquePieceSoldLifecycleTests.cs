using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class UniquePieceSoldLifecycleTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly OrderContactSnapshot Contact = new(
        "Buyer SEO", "+84900000301", "seo@example.invalid",
        "Receiver SEO", "+84900000302", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "12 Duong Tra");
    private static readonly OrderAttributionSnapshot Attribution = new(OrderAcquisitionSource.Direct, true);

    [Fact]
    public async Task ReservedUniquePieceRemainsOutOfStockUntilShipped()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, productId, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 4_000_000L, suffix: "u1");

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "Chot"), CancellationToken.None);
            Assert.True(res.IsSuccess);
        }

        // While reserved (pending/confirmed), unique piece is NOT "sold" yet -> "out_of_stock" and sold_out_at IS NULL.
        var detail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-u1");
        Assert.NotNull(detail);
        Assert.Equal("out_of_stock", Assert.Single(detail.Variants).Availability);

        var page = await app.Client.GetFromJsonAsync<StoreProductPage>("/api/v1/store/products?q=u1");
        Assert.NotNull(page);
        Assert.Equal("out_of_stock", Assert.Single(page.Items).Availability);

        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));
    }

    [Fact]
    public async Task ShippedUniquePieceMarksSoldSetsSoldOutAtAndEmitsInvalidation()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, productId, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 4_500_000L, suffix: "u2");

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            order = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "Chot"), CancellationToken.None)).Value;
        }

        await app.ExecuteAsync($"""
DELETE FROM platform.outbox_messages;
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order.Id}','order','manual','GHN','created',{order.TotalVnd},'{app.OwnerId}');
""");

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var shipped = await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(order.Id, order.Version, app.OwnerId), CancellationToken.None);
            Assert.True(shipped.IsSuccess, shipped.IsSuccess ? "" : shipped.Error.Code);
        }

        var detail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-u2");
        Assert.NotNull(detail);
        Assert.Equal("sold", Assert.Single(detail.Variants).Availability);

        var page = await app.Client.GetFromJsonAsync<StoreProductPage>("/api/v1/store/products?q=u2");
        Assert.NotNull(page);
        Assert.Equal("sold", Assert.Single(page.Items).Availability);

        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NOT NULL FROM catalog.products WHERE id='{productId}'"));

        // R-P205-02: even though available stayed 0 -> 0 during confirmed -> shipping, StorefrontInvalidate was emitted!
        var invalidations = await ScalarAsync<string>(app, $"SELECT coalesce(string_agg(payload::text, ' '), '') FROM platform.outbox_messages WHERE type='{StorefrontInvalidate.Message}'");
        Assert.Contains("product:pot-p2-u2", invalidations, StringComparison.Ordinal);
        Assert.Contains("sitemap", invalidations, StringComparison.Ordinal);
        Assert.Contains("feed", invalidations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiVariantAndDamagedUniquePieceDoNotFalselySetSoldOutAt()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        // 1. Product with 2 unique active variants: selling only 1 keeps sold_out_at NULL.
        var (customerId, productId, v1, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 3_000_000L, suffix: "mv");
        var v2 = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,position,is_unique_piece)
VALUES ('{v2}','{productId}','AM-P2-MV2','active',3200000,1,true);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd) VALUES ('{v2}',1,0,1500000);
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,ref_type,actor_type,occurred_at)
VALUES ('{Guid.CreateVersion7()}','{v2}','opening',1,1500000,1,'import','system',now());
""");

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            order = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(v1, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order.Id}','order','manual','GHN','created',{order.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(order.Id, order.Version, app.OwnerId), CancellationToken.None)).IsSuccess);
        }
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));

        // 2. Product with 0 active variants (R-P205-03 vacuous truth check): SyncSoldOutStateAsync keeps sold_out_at NULL.
        var zeroActiveProduct = Guid.CreateVersion7();
        var typeId = await ScalarAsync<Guid>(app, "SELECT id FROM catalog.product_types LIMIT 1");
        await app.ExecuteAsync($"""
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
VALUES ('{zeroActiveProduct}','{typeId}','AM-XX-9099','pot-p2-zero','Zero Active','active',now(),200,'zero active');
INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,is_unique_piece)
VALUES ('{Guid.CreateVersion7()}','{zeroActiveProduct}','AM-XX-9099','inactive',1000000,true);
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sync = scope.ServiceProvider.GetRequiredService<IProductSoldOutSync>();
            var res = await uow.ExecuteAsync(async ct =>
            {
                await sync.SyncSoldOutStateAsync([zeroActiveProduct], ct);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(res.IsSuccess);
        }
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{zeroActiveProduct}'"));

        // 3. Damaged unique piece -> available == 0, net commercial sales == 0 -> "out_of_stock" and sold_out_at NULL.
        var damagedVariant = await AddProductAndVariantAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 2_800_000L, suffix: "dmg");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();
            var res = await uow.ExecuteAsync(async ct =>
            {
                var locked = await store.LockAsync([damagedVariant], ct);
                Assert.Null(InventoryMutation.Plan(locked[damagedVariant], new("damage", -1, 300_000L, "adjustment", null, "Vo trong kho", "staff", app.OwnerId), out var posting));
                mutation.Stage([posting!]);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(res.IsSuccess);
        }
        var dmgDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-dmg");
        Assert.NotNull(dmgDetail);
        Assert.Equal("out_of_stock", Assert.Single(dmgDetail.Variants).Availability);
    }

    [Fact]
    public async Task ReturnRestockClearsSoldOutAtRestoresInStockAndAllowsResale()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, productId, variantId, _) = await SeedProductVariantAndCustomerAsync(app, isUniquePiece: true, onHand: 1, priceVnd: 5_000_000L, suffix: "rr", avgCostVnd: 2_000_000L);

        // First sale -> sold + sold_out_at set.
        OrderSummary order1;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            order1 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order1.Id}','order','manual','GHN','created',{order1.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(order1.Id, order1.Version, app.OwnerId), CancellationToken.None)).IsSuccess);
        }
        var firstSoldOutAt = await ScalarAsync<DateTime>(app, $"SELECT sold_out_at FROM catalog.products WHERE id='{productId}'");

        // Return restock (ref_type = 'order', e.g., failed delivery returned to shop in P2-08) -> sold_out_at cleared, availability = in_stock, StorefrontInvalidate emitted!
        await app.ExecuteAsync("DELETE FROM platform.outbox_messages;");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var reservations = scope.ServiceProvider.GetRequiredService<IInventoryReservationService>();
            var res = await uow.ExecuteAsync(async ct =>
            {
                await reservations.RestockReturnedAsync(order1.Id, "order", [new(variantId, 1, 2_000_000L)], OrderActorType.Staff, app.OwnerId, "Giao that bai hoan kho", ct);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(res.IsSuccess);
        }

        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));
        var restockedDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-rr");
        Assert.NotNull(restockedDetail);
        Assert.Equal("in_stock", Assert.Single(restockedDetail.Variants).Availability);
        var restockInv = await ScalarAsync<string>(app, $"SELECT coalesce(string_agg(payload::text, ' '), '') FROM platform.outbox_messages WHERE type='{StorefrontInvalidate.Message}'");
        Assert.Contains("product:pot-p2-rr", restockInv, StringComparison.Ordinal);

        // Resale (order 2) -> "sold" again with a fresh sold_out_at, then return restock with ref_type='return' (P2-10) clears it again!
        OrderSummary order2;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            order2 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.ZaloOa, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK2"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order2.Id}','order','manual','GHN','created',{order2.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(order2.Id, order2.Version, app.OwnerId), CancellationToken.None)).IsSuccess);
        }
        var secondSoldOutAt = await ScalarAsync<DateTime>(app, $"SELECT sold_out_at FROM catalog.products WHERE id='{productId}'");
        Assert.True(secondSoldOutAt >= firstSoldOutAt);
        var resoldDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-rr");
        Assert.Equal("sold", Assert.Single(resoldDetail!.Variants).Availability);

        // Return restock with ref_type = 'return' (P2-10 path)
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var reservations = scope.ServiceProvider.GetRequiredService<IInventoryReservationService>();
            var res = await uow.ExecuteAsync(async ct =>
            {
                await reservations.RestockReturnedAsync(Guid.CreateVersion7(), "return", [new(variantId, 1, 2_000_000L)], OrderActorType.Staff, app.OwnerId, "Khach doi tra", ct);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(res.IsSuccess);
        }
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));
    }
}
