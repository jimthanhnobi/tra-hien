using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Outbox;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class OrderLifecycleAndReservationTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly OrderContactSnapshot Contact = new(
        "Nguyen Van Thu Nghiem", "+84900000201", "synthetic@example.invalid",
        "Tran Thi Nhan Hang", "+84900000202", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "99 Duong Bi Mat PII");
    private static readonly OrderAttributionSnapshot Attribution = new(OrderAcquisitionSource.Direct, true);

    private sealed class RecordingStep(string messageType, int order, List<string> log, bool fail = false) : IOrderOutboxStep
    {
        public string MessageType => messageType;
        public int Order => order;
        public Task HandleAsync(Guid messageId, OrderOutboxPayload payload, CancellationToken ct)
        {
            log.Add($"{order}:{messageType}:{payload.OrderCode}");
            if (fail) throw new InvalidOperationException("Simulated outbox step failure.");
            return Task.CompletedTask;
        }
    }

    private sealed class AdjustableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    [Fact]
    public async Task ReserveShortfallRollsBackAllLinesAndReturnsAllInsufficientVariants()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, vOk, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 200_000L, suffix: "ok");
        var vShort1 = await AddProductAndVariantAsync(app, false, onHand: 1, priceVnd: 300_000L, suffix: "s1");
        var vShort2 = await AddProductAndVariantAsync(app, false, onHand: 0, priceVnd: 400_000L, suffix: "s2");

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
        var result = await handler.Handle(new CreateAdminOrder(
            SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
            [new(vOk, 2), new(vShort1, 2), new(vShort2, 1)],
            null, null, null, null, null, Attribution, null, null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("out_of_stock", result.Error.Code);
        var details = OutOfStockErrorFormatting.Parse(result.Error);
        Assert.Equal(2, details.Count);
        Assert.Contains(details, d => d.VariantId == vShort1 && d.Available == 1);
        Assert.Contains(details, d => d.VariantId == vShort2 && d.Available == 0);

        // Full rollback: zero orders, zero reservations, vOk reserved remains 0.
        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.orders"));
        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.reservations"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vOk}'"));
    }

    [Fact]
    public async Task AcP202CancelPendingAndConfirmedReleasesReservedWithoutMovementsAndRevertsVoucher()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 600_000L, suffix: "c1");
        var voucherId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,usage_limit,per_customer_limit,status)
VALUES ('{voucherId}','CANCEL100','manual','fixed',100000,500000,now() - interval '1 day',now() + interval '10 day',5,2,'active');
""");

        // 1. Place pending order with voucher -> cancel pending
        OrderSummary pendingOrder;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
            var res = await create.Handle(new CreateWebOrder(
                customerId, null, OrderPaymentMethod.Cod, Contact, [new(variantId, 2)],
                "CANCEL100", null, null, Attribution, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            pendingOrder = res.Value;
        }
        Assert.Equal(2, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{voucherId}'"));
        var movesBeforeCancel = await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.stock_movements");

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            var res = await cancel.Handle(new CancelOrder(pendingOrder.Id, pendingOrder.Version, OrderActorType.Staff, app.OwnerId, "Khach huy"), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            Assert.Equal(OrderStatus.Cancelled, res.Value.Status);
        }

        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(movesBeforeCancel, await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.stock_movements"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{voucherId}'"));
        Assert.Equal("reverted", await ScalarAsync<string>(app, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id='{pendingOrder.Id}'"));
        Assert.Equal("released", await ScalarAsync<string>(app, $"SELECT status FROM inventory.reservations WHERE order_line_id='{pendingOrder.Lines[0].Id}'"));

        // 2. Create confirmed Admin order -> cancel confirmed (no active shipment)
        OrderSummary confirmedOrder;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact, [new(variantId, 1)],
                null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "Da goi"), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            confirmedOrder = res.Value;
        }
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            var res = await cancel.Handle(new CancelOrder(confirmedOrder.Id, confirmedOrder.Version, OrderActorType.Staff, app.OwnerId, "Huy truoc giao"), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
        }
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(movesBeforeCancel, await ScalarAsync<long>(app, "SELECT count(*) FROM inventory.stock_movements"));
    }

    [Fact]
    public async Task AcP203MarkShippingConsumesReservationWritesSaleMovementAndLocksUnitCost()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        const long initialAvgCost = 420_000L;
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 900_000L, suffix: "sh", avgCostVnd: initialAvgCost);

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.ZaloOa, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact, [new(variantId, 2)],
                null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "Chot"), CancellationToken.None);
            Assert.True(res.IsSuccess);
            order = res.Value;
        }

        // Seed order shipment (BR-SHP-02 precondition for confirmed -> shipping).
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order.Id}','order','manual','GHN','created',{order.TotalVnd},'{app.OwnerId}');
""");

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var ship = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>();
            var res = await ship.Handle(new MarkOrderShipping(order.Id, order.Version, app.OwnerId, "Giao GHN"), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            Assert.Equal(OrderStatus.Shipping, res.Value.Status);
            Assert.Equal(initialAvgCost, Assert.Single(res.Value.Lines).UnitCostVnd);
        }

        Assert.Equal(3, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal("consumed", await ScalarAsync<string>(app, $"SELECT status FROM inventory.reservations WHERE order_line_id='{order.Lines[0].Id}'"));
        Assert.Equal(-2, await ScalarAsync<int>(app, $"SELECT quantity FROM inventory.stock_movements WHERE movement_type='sale' AND ref_type='order' AND ref_id='{order.Id}'"));
        Assert.Equal(initialAvgCost, await ScalarAsync<long>(app, $"SELECT unit_cost_vnd FROM inventory.stock_movements WHERE movement_type='sale' AND ref_type='order' AND ref_id='{order.Id}'"));

        // Receive a new batch at a completely different unit cost -> locked order_lines.unit_cost_vnd MUST NOT change.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();
            var posted = await uow.ExecuteAsync(async ct =>
            {
                var locked = await store.LockAsync([variantId], ct);
                Assert.Null(InventoryMutation.Plan(locked[variantId], new("receipt", 5, 999_000L, "receipt", Guid.CreateVersion7(), null, "staff", app.OwnerId), out var posting));
                mutation.Stage([posting!]);
                return TeaPremium.Application.Results.Result.Success(true);
            }, CancellationToken.None);
            Assert.True(posted.IsSuccess);
        }
        Assert.Equal(initialAvgCost, await ScalarAsync<long>(app, $"SELECT unit_cost_vnd FROM sales.order_lines WHERE id='{order.Lines[0].Id}'"));
    }

    [Fact]
    public async Task DeleteAndAdjustLinesInPendingAndConfirmedSetsReservationOrderLineNullOnDelete()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, vA, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "la");
        var vB = await AddProductAndVariantAsync(app, false, onHand: 10, priceVnd: 300_000L, suffix: "lb");
        var vC = await AddProductAndVariantAsync(app, false, onHand: 10, priceVnd: 200_000L, suffix: "lc");

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                [new(vA, 4), new(vB, 2)], null, null, null, null, null, Attribution, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess);
            order = res.Value;
        }
        var removedLineBId = order.Lines.Single(l => l.VariantId == vB).Id;

        // Pending: reduce vA (4 -> 2), delete vB, add vC (3).
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var res = await update.Handle(new UpdateOrderLines(
                order.Id, order.Version, true, [new(vA, 2), new(vC, 3)], null, null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }

        // Verify vB line is DELETED from sales.order_lines and its reservation is 'released' with order_line_id IS NULL!
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_lines WHERE id='{removedLineBId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.reservations WHERE variant_id='{vB}' AND status='released' AND order_line_id IS NULL AND close_reason='line_removed'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vB}'"));

        // Verify vA has 1 released (quantity_reduced, qty=4) and 1 active (qty=2), reserved == 2.
        Assert.Equal(2, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vA}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.reservations WHERE variant_id='{vA}' AND status='released' AND close_reason='quantity_reduced'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.reservations WHERE variant_id='{vA}' AND status='active' AND quantity=2"));

        // Verify vC has 1 active (qty=3), reserved == 3.
        Assert.Equal(3, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vC}'"));

        // Confirm order, then increase vA (2 -> 5) and delete vC in 'confirmed' state!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var confirm = scope.ServiceProvider.GetRequiredService<ICommandHandler<ConfirmOrder, OrderSummary>>();
            var cRes = await confirm.Handle(new ConfirmOrder(order.Id, order.Version, OrderActorType.Staff, app.OwnerId, true, "OK"), CancellationToken.None);
            Assert.True(cRes.IsSuccess);
            order = cRes.Value;
        }

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var res = await update.Handle(new UpdateOrderLines(
                order.Id, order.Version, true, [new(vA, 5)], null, null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
        }

        Assert.Equal(5, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vA}'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{vC}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.reservations WHERE variant_id='{vC}' AND status='released' AND order_line_id IS NULL"));
    }

    [Fact]
    public async Task ForbidLineDeleteAfterShippingAndVerifyDbCheckSafetyNetForActiveOrConsumed()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, vA, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 500_000L, suffix: "fs");

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                [new(vA, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None);
            Assert.True(res.IsSuccess);
            order = res.Value;
        }

        // Raw SQL DELETE FROM sales.order_lines as tp_app while reservation is 'active' is blocked by 23514!
        await CrmSalesFinanceFixture.ErrorAsync(app.RuntimeConnection,
            $"DELETE FROM sales.order_lines WHERE id='{order.Lines[0].Id}'", "23514", "ck_reservations_order_line_required");

        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{order.Id}','order','manual','GHN','created',{order.TotalVnd},'{app.OwnerId}');
""");

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var ship = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>();
            var sRes = await ship.Handle(new MarkOrderShipping(order.Id, order.Version, app.OwnerId), CancellationToken.None);
            Assert.True(sRes.IsSuccess);
            order = sRes.Value;
        }

        // Domain blocks UpdateOrderLines at shipping.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var uRes = await update.Handle(new UpdateOrderLines(
                order.Id, order.Version, true, [new(vA, 2)], null, null, null, null, null), CancellationToken.None);
            Assert.False(uRes.IsSuccess);
            Assert.Equal("invalid_status_transition", uRes.Error.Code);
        }

        // Raw SQL DELETE FROM sales.order_lines as tp_app while reservation is 'consumed' is ALSO blocked by 23514!
        await CrmSalesFinanceFixture.ErrorAsync(app.RuntimeConnection,
            $"DELETE FROM sales.order_lines WHERE id='{order.Lines[0].Id}'", "23514", "ck_reservations_order_line_required");
    }

    [Fact]
    public async Task XminConcurrencyConflictAuditPiiRedactionVariantUsageAndAppendOnlyHistory()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 500_000L, suffix: "au");

        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                [new(variantId, 1)], null, null, null, null, null, Attribution,
                CustomerNote: "Ghi chu khach PII", InternalNote: "Ghi chu noi bo bi mat"), CancellationToken.None);
            Assert.True(res.IsSuccess);
            order = res.Value;
        }

        // 1. Stale xmin -> concurrency_conflict
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var confirm = scope.ServiceProvider.GetRequiredService<ICommandHandler<ConfirmOrder, OrderSummary>>();
            var stale = await confirm.Handle(new ConfirmOrder(order.Id, order.Version + 99, OrderActorType.Staff, app.OwnerId, true, "OK"), CancellationToken.None);
            Assert.False(stale.IsSuccess);
            Assert.Equal("concurrency_conflict", stale.Error.Code);
        }

        // 2. Audit logs for sales.orders redact all PII & free-text notes
        var auditJson = await ScalarAsync<string>(app, $"SELECT changes::text FROM platform.audit_logs WHERE entity_type='sales.orders' AND entity_id='{order.Id}' LIMIT 1");
        Assert.DoesNotContain(Contact.ContactPhone, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Contact.ContactName, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Contact.ContactEmail!, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Contact.ReceiverName, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Contact.AddressLine, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Ghi chu khach PII", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Ghi chu noi bo bi mat", auditJson, StringComparison.Ordinal);
        Assert.Contains(order.Code, auditJson, StringComparison.Ordinal);

        // 3. OrderVariantUsage reports variant as used (BR-CAT-02 / BR-CAT-06)
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var usages = scope.ServiceProvider.GetServices<IVariantUsage>().ToArray();
            Assert.Contains(usages, u => u.GetType().Name == "OrderVariantUsage");
            var orderUsage = usages.Single(u => u.GetType().Name == "OrderVariantUsage");
            Assert.True(await orderUsage.UsedAsync(variantId, CancellationToken.None));
        }

        // 4. tp_app cannot DELETE sales.orders or sales.order_status_history (42501)
        await CrmSalesFinanceFixture.ErrorAsync(app.RuntimeConnection, $"DELETE FROM sales.orders WHERE id='{order.Id}'", "42501");
        await CrmSalesFinanceFixture.ErrorAsync(app.RuntimeConnection, $"DELETE FROM sales.order_status_history WHERE order_id='{order.Id}'", "42501");
    }

    [Fact]
    public async Task CompositeOrderOutboxHandlersRegisterSingleHandlerPerTypeAndProcessAllSixLifecycleEvents()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToArray();
            foreach (var messageType in OrderEventTypes.All)
                Assert.Single(handlers, h => h.MessageType == messageType);
        }

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "ob");

        // Order 1: Placed -> Confirmed -> Shipping -> Delivered
        OrderSummary o1;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            o1 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>()
                .Handle(new CreateWebOrder(customerId, null, OrderPaymentMethod.Cod, Contact, [new(variantId, 1)], null, null, null, Attribution, "secret note"), CancellationToken.None)).Value;
            o1 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<ConfirmOrder, OrderSummary>>()
                .Handle(new ConfirmOrder(o1.Id, o1.Version, OrderActorType.Staff, app.OwnerId, true, "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{o1.Id}','order','manual','GHN','created',{o1.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            o1 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(o1.Id, o1.Version, app.OwnerId), CancellationToken.None)).Value;
            o1 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderDelivered, OrderSummary>>()
                .Handle(new MarkOrderDelivered(o1.Id, o1.Version, OrderActorType.Staff, app.OwnerId, DateTimeOffset.UtcNow), CancellationToken.None)).Value;
        }

        // Order 2: Placed -> Cancelled
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var o2 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>()
                .Handle(new CreateWebOrder(customerId, null, OrderPaymentMethod.Cod, Contact, [new(variantId, 1)], null, null, null, Attribution, null), CancellationToken.None)).Value;
            await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(o2.Id, o2.Version, OrderActorType.Staff, app.OwnerId, "Huy thu nghiem"), CancellationToken.None);
        }

        // Order 3: Placed -> Confirmed -> Shipping -> DeliveryFailed
        OrderSummary o3;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            o3 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact, [new(variantId, 1)],
                    null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{o3.Id}','order','manual','GHN','created',{o3.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            o3 = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(o3.Id, o3.Version, app.OwnerId), CancellationToken.None)).Value;
            await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderDeliveryFailed, OrderSummary>>()
                .Handle(new MarkOrderDeliveryFailed(o3.Id, o3.Version, OrderActorType.Staff, app.OwnerId, "Khach khong nghe may"), CancellationToken.None);
        }

        // Remove non-Order outbox messages (e.g. StorefrontInvalidate) so we test Order* processing cleanly.
        await app.ExecuteAsync("DELETE FROM platform.outbox_messages WHERE type NOT LIKE 'Order%';");

        var payloadsConcat = await ScalarAsync<string>(app, "SELECT coalesce(string_agg(payload::text, ' '), '') FROM platform.outbox_messages");
        Assert.DoesNotContain(Contact.ContactPhone, payloadsConcat, StringComparison.Ordinal);
        Assert.DoesNotContain(Contact.ContactName, payloadsConcat, StringComparison.Ordinal);
        Assert.DoesNotContain("secret note", payloadsConcat, StringComparison.Ordinal);
        Assert.DoesNotContain("Khach khong nghe may", payloadsConcat, StringComparison.Ordinal);

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var processed = await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(50, CancellationToken.None);
            Assert.True(processed >= 6);
        }

        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM platform.outbox_messages WHERE status <> 'processed'"));
        foreach (var type in OrderEventTypes.All)
            Assert.True(await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{type}' AND status='processed'") >= 1L);
    }

    [Fact]
    public async Task OrderOutboxExecutesStepsInOrderAndRetriesMalformedOrFailingStepToDead()
    {
        var clock = new AdjustableClock(new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IOrderOutboxStep>(new RecordingStep(OrderEventTypes.OrderPlaced, 20, log));
            services.AddSingleton<IOrderOutboxStep>(new RecordingStep(OrderEventTypes.OrderPlaced, 10, log));
            services.AddSingleton<IOrderOutboxStep>(new RecordingStep(OrderEventTypes.OrderCancelled, 1, log, fail: true));
        });

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 5, priceVnd: 500_000L, suffix: "st");
        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            order = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>()
                .Handle(new CreateWebOrder(customerId, null, OrderPaymentMethod.Cod, Contact, [new(variantId, 1)], null, null, null, Attribution, null), CancellationToken.None)).Value;
            await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(order.Id, order.Version, OrderActorType.Staff, app.OwnerId, "Test"), CancellationToken.None);
        }

        // Inject a malformed OrderPlaced message too.
        var badId = Guid.CreateVersion7();
        await app.ExecuteAsync($$"""
DELETE FROM platform.outbox_messages WHERE type NOT LIKE 'Order%';
INSERT INTO platform.outbox_messages(id,type,payload,occurred_at,available_at,attempts,status)
VALUES ('{{badId}}','OrderPlaced','{"orderId":"{{order.Id}}","unknownField":123}'::jsonb,'{{clock.UtcNow:O}}','{{clock.UtcNow:O}}',0,'pending');
""");

        // Run 10 backoff cycles: valid OrderPlaced succeeds once with steps ordered (10 then 20);
        // failing OrderCancelled and malformed OrderPlaced exhaust 10 attempts -> dead.
        for (var i = 0; i < 11; i++)
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(20, CancellationToken.None);
            clock.UtcNow = clock.UtcNow.AddHours(7);
        }

        Assert.Equal([$"10:OrderPlaced:{order.Code}", $"20:OrderPlaced:{order.Code}"], log.Where(x => x.Contains("OrderPlaced", StringComparison.Ordinal)).ToArray());
        Assert.Equal("dead", await ScalarAsync<string>(app, $"SELECT status FROM platform.outbox_messages WHERE id='{badId}'"));
        Assert.Equal(10, await ScalarAsync<int>(app, $"SELECT attempts FROM platform.outbox_messages WHERE id='{badId}'"));
        Assert.Equal("dead", await ScalarAsync<string>(app, "SELECT status FROM platform.outbox_messages WHERE type='OrderCancelled'"));
        Assert.Equal(10, await ScalarAsync<int>(app, "SELECT attempts FROM platform.outbox_messages WHERE type='OrderCancelled'"));
    }

    [Fact]
    public async Task UpdateOrderLinesSynchronizesVoucherApplyKeepDropReapplySwapAndCancelIdempotently()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "vu");
        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,usage_limit,per_customer_limit,status)
VALUES
 ('{v1}','EDITPCT10','manual','percent',10,1000000,now() - interval '1 day',now() + interval '10 day',1,1,'active'),
 ('{v2}','EDITFIX50','manual','fixed',50000,500000,now() - interval '1 day',now() + interval '10 day',5,1,'active');
""");

        // Create order WITHOUT voucher (qty=2 -> subtotal 1,000,000).
        OrderSummary order;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 2)], null, null, null, null, null, Attribution, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Null(order.VoucherId);
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));

        // (1) Apply v1 via UpdateOrderLines -> used_count = 1, redemption applied with amount_vnd = 100,000.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 2)], "EDITPCT10", null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Equal(v1, order.VoucherId);
        Assert.Equal(100_000L, order.DiscountVnd);
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal("applied", await ScalarAsync<string>(app, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));
        Assert.Equal(100_000L, await ScalarAsync<long>(app, $"SELECT amount_vnd FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        // (3a) Keep same v1 (per_customer_limit=1, usage_limit=1) while editing qty 2 -> 3 -> succeeds, amount_vnd = 150,000, used_count still 1!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 3)], "EDITPCT10", null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Equal(v1, order.VoucherId);
        Assert.Equal(150_000L, order.DiscountVnd);
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal(150_000L, await ScalarAsync<long>(app, $"SELECT amount_vnd FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        // (G12=A / BR-ORD-08) Reduce qty 3 -> 1 (subtotal 500,000 < min_order 1,000,000) while keeping v1 -> auto-drop voucher & revert!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 1)], "EDITPCT10", null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Null(order.VoucherId);
        Assert.Equal(0L, order.DiscountVnd);
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal("reverted", await ScalarAsync<string>(app, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        // (3b) Re-apply v1 on the SAME order (existing reverted row) after restoring qty -> 2: UPDATEs existing row, no 23505!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 2)], "EDITPCT10", null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Equal(v1, order.VoucherId);
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal("applied", await ScalarAsync<string>(app, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        // Swap v1 -> v2 via UpdateOrderLines -> v1 reverted (used_count=0), v2 applied (used_count=1) on the same redemption row.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 2)], "EDITFIX50", null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Equal(v2, order.VoucherId);
        Assert.Equal(50_000L, order.DiscountVnd);
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal(1, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v2}'"));
        Assert.Equal(v2, await ScalarAsync<Guid>(app, $"SELECT voucher_id FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        // (2) Explicitly remove voucher (VoucherCode = null) -> reverted, used_count = 0; subsequent CancelOrder does NOT decrement again!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>()
                .Handle(new UpdateOrderLines(order.Id, order.Version, true, [new(variantId, 2)], null, null, null, null, null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            order = res.Value;
        }
        Assert.Null(order.VoucherId);
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v2}'"));
        Assert.Equal("reverted", await ScalarAsync<string>(app, $"SELECT status FROM pricing.voucher_redemptions WHERE order_id='{order.Id}'"));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancelled = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(order.Id, order.Version, OrderActorType.Staff, app.OwnerId, "Huy sau khi go ma"), CancellationToken.None);
            Assert.True(cancelled.IsSuccess, cancelled.IsSuccess ? "" : cancelled.Error.Code);
        }
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v1}'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT used_count FROM pricing.vouchers WHERE id='{v2}'"));
    }

    [Fact]
    public async Task ActiveOrderShipmentFilterMatchesUxShipmentActiveOrderAndOrderSettingsClampLargeInt64()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "sf");

        // F4: settings > Int32.MaxValue are safely read via GetInt64() and clamped instead of throwing.
        await app.ExecuteAsync("""
INSERT INTO platform.settings(key,value) VALUES
 ('orders.bank_transfer_hold_hours','5000000000'::jsonb),
 ('orders.max_quantity_per_line_web','5000000000'::jsonb),
 ('orders.max_staff_discount_percent','5000000000'::jsonb)
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var settings = await scope.ServiceProvider.GetRequiredService<IOrderPricingRead>().GetOrderSettingsAsync(CancellationToken.None);
            Assert.Equal(24 * 365, settings.BankTransferHoldHours);
            Assert.Equal(1000, settings.MaxQuantityPerLineWeb);
            Assert.Equal(100, settings.MaxStaffDiscountPercent);
        }

        // F3: failed / returned / cancelled order shipments do NOT count as active order shipments.
        OrderSummary orderWithInactiveShipments;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            orderWithInactiveShipments = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by) VALUES
 ('{Guid.CreateVersion7()}','{orderWithInactiveShipments.Id}','order','manual','GHN','cancelled',0,'{app.OwnerId}'),
 ('{Guid.CreateVersion7()}','{orderWithInactiveShipments.Id}','order','manual','GHN','failed',0,'{app.OwnerId}'),
 ('{Guid.CreateVersion7()}','{orderWithInactiveShipments.Id}','order','manual','GHN','returned',0,'{app.OwnerId}');
""");

        // MarkShipping rejects because none of the shipments are active; CancelOrder from confirmed succeeds!
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var shipRes = await scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>()
                .Handle(new MarkOrderShipping(orderWithInactiveShipments.Id, orderWithInactiveShipments.Version, app.OwnerId), CancellationToken.None);
            Assert.False(shipRes.IsSuccess);
            Assert.Equal("invalid_status_transition", shipRes.Error.Code);

            var cancelRes = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(orderWithInactiveShipments.Id, orderWithInactiveShipments.Version, OrderActorType.Staff, app.OwnerId, "Khong co van don hoat dong"), CancellationToken.None);
            Assert.True(cancelRes.IsSuccess, cancelRes.IsSuccess ? "" : cancelRes.Error.Code);
        }

        // Confirmed order with an active ('created') order shipment CANNOT be cancelled.
        OrderSummary orderWithActiveShipment;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            orderWithActiveShipment = (await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                .Handle(new CreateAdminOrder(SalesChannel.Phone, customerId, app.OwnerId, true, OrderPaymentMethod.Cod, Contact,
                    [new(variantId, 1)], null, null, null, null, null, Attribution, null, null, ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None)).Value;
        }
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{orderWithActiveShipment.Id}','order','manual','GHN','created',{orderWithActiveShipment.TotalVnd},'{app.OwnerId}');
""");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancelRes = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(orderWithActiveShipment.Id, orderWithActiveShipment.Version, OrderActorType.Staff, app.OwnerId, "Thu huy"), CancellationToken.None);
            Assert.False(cancelRes.IsSuccess);
            Assert.Equal("invalid_status_transition", cancelRes.Error.Code);
        }
    }
}
