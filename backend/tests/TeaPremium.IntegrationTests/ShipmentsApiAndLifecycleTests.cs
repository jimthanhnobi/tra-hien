using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Inventory;
using TeaPremium.Infrastructure.Outbox;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class ShipmentsApiAndLifecycleTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed class AdjustableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    private sealed class RecordingDeliveredStep(List<string> log) : IOrderOutboxStep
    {
        public string MessageType => OrderEventTypes.OrderDelivered;
        public int Order => 50;
        public Task HandleAsync(Guid messageId, OrderOutboxPayload payload, CancellationToken ct)
        {
            log.Add($"{payload.OrderCode}:{payload.Status}");
            return Task.CompletedTask;
        }
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string? csrfToken = null)
    {
        var token = csrfToken ?? await GetCsrfTokenAsync(client);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("X-Correlation-ID", "4b8f19d0-1a90-4a91-a211-94e162e59f01");
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> SendRawJsonWithCsrfAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string rawJson,
        string? csrfToken = null)
    {
        var token = csrfToken ?? await GetCsrfTokenAsync(client);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("X-Correlation-ID", "4b8f19d0-1a90-4a91-a211-94e162e59f01");
        return await client.SendAsync(req);
    }

    // I01: AC-P2-03 & BR-SHP-02
    [Fact]
    public async Task AcP203CreateShipmentTransitionsOrderToShippingConsumesStockLocksUnitCostAndEmitsOutbox()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        const long initialAvgCost = 420_000L;
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 900_000L, suffix: "shp01", avgCostVnd: initialAvgCost);

        var order = await CreateAdminOrderAsync(
            app, customerId, variantId, quantity: 2, paymentMethod: OrderPaymentMethod.Cod,
            overrideShippingFeeVnd: 30_000L, confirmNow: true);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(1_830_000L, order.TotalVnd);

        clock.UtcNow = clock.UtcNow.AddMinutes(15);
        using var createResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new
        {
            carrierName = "  Giao Hang Nhanh  ",
            trackingCode = "  GHN-261126-001  ",
            shippingCostVnd = 28_000L,
            note = "Goi truoc khi giao"
        });

        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var shipment = (await createResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
        Assert.Equal($"/api/v1/admin/shipments/{shipment.Id}", createResp.Headers.Location?.ToString());
        Assert.Equal(order.Id, shipment.OrderId);
        Assert.Equal(order.Code, shipment.OrderCode);
        Assert.Equal(OrderStatus.Shipping, shipment.OrderStatus);
        Assert.Equal(ShipmentPurpose.Order, shipment.Purpose);
        Assert.Equal(ShippingProviderCode.Manual, shipment.Provider);
        Assert.Equal("Giao Hang Nhanh", shipment.CarrierName);
        Assert.Equal("GHN-261126-001", shipment.TrackingCode);
        Assert.Equal(ShipmentStatus.Created, shipment.Status);
        Assert.Equal(1_830_000L, shipment.CodAmountVnd);
        Assert.Equal(28_000L, shipment.ShippingCostVnd);
        Assert.Null(shipment.ReturnCostVnd);

        var initEvt = Assert.Single(shipment.Events);
        Assert.Equal(ShipmentStatus.Created, initEvt.Status);
        Assert.Equal(ShipmentEventSource.Manual, initEvt.Source);
        Assert.Equal("Goi truoc khi giao", initEvt.Note);
        Assert.Equal(shipment.CreatedAt, initEvt.OccurredAt);

        // AC-P2-03 assertions on DB state.
        Assert.Equal(3, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal("consumed", await ScalarAsync<string>(app, $"SELECT status FROM inventory.reservations WHERE order_line_id='{order.Lines[0].Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.stock_movements WHERE movement_type='sale' AND ref_type='order' AND ref_id='{order.Id}'"));
        Assert.Equal(-2, await ScalarAsync<int>(app, $"SELECT quantity FROM inventory.stock_movements WHERE movement_type='sale' AND ref_type='order' AND ref_id='{order.Id}'"));
        Assert.Equal(initialAvgCost, await ScalarAsync<long>(app, $"SELECT unit_cost_vnd FROM inventory.stock_movements WHERE movement_type='sale' AND ref_type='order' AND ref_id='{order.Id}'"));
        Assert.Equal(initialAvgCost, await ScalarAsync<long>(app, $"SELECT unit_cost_vnd FROM sales.order_lines WHERE id='{order.Lines[0].Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_status_history WHERE order_id='{order.Id}' AND from_status='confirmed' AND to_status='shipping'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderShipped}' AND payload->>'orderId'='{order.Id}'"));

        // New receipt batch at a different unit cost MUST NOT mutate locked order_lines.unit_cost_vnd.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();
            var posted = await uow.ExecuteAsync(async ct =>
            {
                var locked = await store.LockAsync([variantId], ct);
                Assert.Null(InventoryMutation.Plan(locked[variantId], new("receipt", 5, 888_000L, "receipt", Guid.CreateVersion7(), null, "staff", app.OwnerId), out var posting));
                mutation.Stage([posting!]);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(posted.IsSuccess);
        }
        Assert.Equal(initialAvgCost, await ScalarAsync<long>(app, $"SELECT unit_cost_vnd FROM sales.order_lines WHERE id='{order.Lines[0].Id}'"));

        // Verify IOrderShipmentQueries read port (for P2-06).
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var list = await scope.ServiceProvider.GetRequiredService<IOrderShipmentQueries>().ListByOrderIdAsync(order.Id, CancellationToken.None);
            var item = Assert.Single(list);
            Assert.Equal(shipment.Id, item.Id);
            Assert.Single(item.Events);
        }
    }

    // I02
    [Fact]
    public async Task CreateShipmentRejectsUnconfirmedOrUnpaidBankTransferOrderWithZeroSideEffects()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, suffix: "shp02");

        // 1. Pending order -> 409 invalid_status_transition.
        var pendingOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: false);
        using (var r1 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{pendingOrder.Id}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(r1, HttpStatusCode.Conflict, "invalid_status_transition");

        // 2. Cancelled order -> 409 invalid_status_transition.
        var cancelOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: false);
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>()
                .Handle(new CancelOrder(cancelOrder.Id, cancelOrder.Version, OrderActorType.Staff, app.OwnerId, "Khach huy"), CancellationToken.None);
            Assert.True(cancel.IsSuccess);
        }
        using (var r2 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{cancelOrder.Id}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(r2, HttpStatusCode.Conflict, "invalid_status_transition");

        // 3. Confirmed bank_transfer order that is NOT paid -> 409 invalid_status_transition.
        var unpaidBankConfirmed = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, confirmNow: true);
        using (var r3 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{unpaidBankConfirmed.Id}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(r3, HttpStatusCode.Conflict, "invalid_status_transition");

        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.shipments"));
        Assert.Equal(10, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(2, await ScalarAsync<int>(app, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
    }

    // I03: BR-SHP-04 active order shipment & tracking uniqueness + concurrent SAVEPOINT race
    [Fact]
    public async Task BrShp04ActiveOrderShipmentAndTrackingUniquenessEnforcedIncludingConcurrentRaceViaSavepoint()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 20, priceVnd: 400_000L, suffix: "shp03");

        // 1. Seeded active shipment on confirmed order exercises SAVEPOINT 23505 ux_shipment_active_order cleanly (0 25P02, 0 audit leak).
        var seededOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_by)
VALUES ('{Guid.CreateVersion7()}','{seededOrder.Id}','order','manual','GHN','created',{seededOrder.TotalVnd},'{app.OwnerId}');
""");
        using (var savepointDup = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{seededOrder.Id}/shipments", new { carrierName = "GHN", trackingCode = "GHN-SAVEPOINT-1" }))
            await AssertProblemAsync(savepointDup, HttpStatusCode.Conflict, "active_shipment_exists");
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.audit_logs WHERE entity_type='sales.shipments' AND changes::text LIKE '%{seededOrder.Id}%'"));

        // 2. 8 concurrent POST .../shipments on the same confirmed order -> exact 1 x 201 and 7 x 409, stock deducted once!
        var raceOrder = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 2, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        const int workers = 8;
        var clients = new List<(HttpClient Client, string Csrf)>(workers);
        try
        {
            for (var i = 0; i < workers; i++)
            {
                var c = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(c, "owner@example.test", P009Harness.Password);
                var csrf = await GetCsrfTokenAsync(c);
                clients.Add((c, csrf));
            }

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = 0;
            var responses = await Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref ready) == workers) startGate.TrySetResult();
                await startGate.Task;
                using var resp = await SendWithCsrfAsync(
                    clients[i].Client,
                    HttpMethod.Post,
                    $"/api/v1/admin/orders/{raceOrder.Id}/shipments",
                    new { carrierName = "GHN", trackingCode = $"GHN-RACE-{i}" },
                    clients[i].Csrf);
                var body = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, body);
            })));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(workers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.All(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict), r =>
            {
                using var doc = JsonDocument.Parse(r.body);
                Assert.Equal("active_shipment_exists", doc.RootElement.GetProperty("code").GetString());
            });
            Assert.Equal(18, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
            Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.shipments WHERE order_id='{raceOrder.Id}'"));
        }
        finally
        {
            foreach (var (c, _) in clients) c.Dispose();
        }

        // Sequential second call on already-shipping order -> 409 active_shipment_exists.
        using (var seqDup = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{raceOrder.Id}/shipments", new { carrierName = "GHN", trackingCode = "GHN-SEQ-2" }))
            await AssertProblemAsync(seqDup, HttpStatusCode.Conflict, "active_shipment_exists");

        // 3. Duplicate (carrierName, trackingCode) on POST & PATCH -> 409 tracking_code_exists; null trackingCode allowed for multiple orders.
        var oA = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        var oB = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        var oC = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);

        using var sA = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{oA.Id}/shipments", new { carrierName = "GHTK", trackingCode = "GHTK-UNIQ-01" });
        Assert.Equal(HttpStatusCode.Created, sA.StatusCode);

        using (var dupPost = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{oB.Id}/shipments", new { carrierName = "GHTK", trackingCode = "GHTK-UNIQ-01" }))
            await AssertProblemAsync(dupPost, HttpStatusCode.Conflict, "tracking_code_exists");

        // Blank/null trackingCode on both oB and oC for the same carrier succeeds!
        using var sBResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{oB.Id}/shipments", new { carrierName = "GHTK", trackingCode = "   " });
        Assert.Equal(HttpStatusCode.Created, sBResp.StatusCode);
        var sB = (await sBResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
        Assert.Null(sB.TrackingCode);

        using var sCResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{oC.Id}/shipments", new { carrierName = "GHTK" });
        Assert.Equal(HttpStatusCode.Created, sCResp.StatusCode);

        // PATCH trackingCode on sB to collide with sA -> 409 tracking_code_exists (via SAVEPOINT).
        using (var dupPatch = await app.MutateAsync(HttpMethod.Patch, $"/api/v1/admin/shipments/{sB.Id}", new { trackingCode = "GHTK-UNIQ-01" }))
            await AssertProblemAsync(dupPatch, HttpStatusCode.Conflict, "tracking_code_exists");
    }

    // I04: PATCH partial updates, null clearing, BR-SHP-05 post-delivery cost entry, and PII-redacted audit logs
    [Fact]
    public async Task PatchShipmentPartialUpdateAndClearNullableFieldsWorksAcrossLifecycleAndAuditsRedacted()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 600_000L, suffix: "shp04");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);

        const string secretDriverCarrier = "Xe Om Anh Hung 0909888777";
        const string secretTracking = "TRACK-PII-0909888777";
        const string secretNote = "Goi chi Hoa 0912345678 truoc khi giao";

        using var createdResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new
        {
            carrierName = secretDriverCarrier,
            trackingCode = secretTracking,
            shippingCostVnd = 25_000L,
            note = secretNote
        });
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
        var shipment = (await createdResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        // Reject empty body, unknown field, empty string trackingCode, negative cost -> 400 validation_failed.
        using (var r1 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", "{}"))
            await AssertProblemAsync(r1, HttpStatusCode.BadRequest, "validation_failed");
        using (var r2 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"unknownField":1}"""))
            await AssertProblemAsync(r2, HttpStatusCode.BadRequest, "validation_failed");
        using (var r3 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"trackingCode":"   "}"""))
            await AssertProblemAsync(r3, HttpStatusCode.BadRequest, "validation_failed");
        using (var r4 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"shippingCostVnd":-1}"""))
            await AssertProblemAsync(r4, HttpStatusCode.BadRequest, "validation_failed");

        // Partial update only trackingCode -> keeps shippingCostVnd.
        using (var p1 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"trackingCode":"  NEW-TRACK-777  "}"""))
        {
            Assert.Equal(HttpStatusCode.OK, p1.StatusCode);
            var dto = (await p1.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Equal("NEW-TRACK-777", dto.TrackingCode);
            Assert.Equal(25_000L, dto.ShippingCostVnd);
        }

        // Clear trackingCode to null explicitly.
        using (var p2 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"trackingCode":null}"""))
        {
            Assert.Equal(HttpStatusCode.OK, p2.StatusCode);
            var dto = (await p2.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Null(dto.TrackingCode);
            Assert.Equal(25_000L, dto.ShippingCostVnd);
        }

        // Deliver shipment, then update shippingCostVnd post-delivery (BR-SHP-05).
        clock.UtcNow = clock.UtcNow.AddHours(2);
        using (var del = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "delivered",
            occurredAt = clock.UtcNow.AddMinutes(-5).ToString("O")
        }))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        using (var p3 = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", """{"shippingCostVnd":31500,"returnCostVnd":0}"""))
        {
            Assert.Equal(HttpStatusCode.OK, p3.StatusCode);
            var dto = (await p3.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Equal(31_500L, dto.ShippingCostVnd);
            Assert.Equal(0L, dto.ReturnCostVnd);
        }

        // Verify platform.audit_logs on sales.shipments: records numeric/status/defaulted create fields and redacts PII!
        var auditChanges = await ScalarAsync<string>(app, $"SELECT string_agg(changes::text, ' ') FROM platform.audit_logs WHERE entity_type='sales.shipments' AND entity_id='{shipment.Id}'");
        Assert.Contains("\"purpose\"", auditChanges, StringComparison.Ordinal);
        Assert.Contains("\"provider\"", auditChanges, StringComparison.Ordinal);
        Assert.Contains("\"cod_amount_vnd\"", auditChanges, StringComparison.Ordinal);
        Assert.Contains("31500", auditChanges, StringComparison.Ordinal);
        Assert.Contains("\"[redacted]\"", auditChanges, StringComparison.Ordinal);
        Assert.DoesNotContain(secretDriverCarrier, auditChanges, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTracking, auditChanges, StringComparison.Ordinal);
        Assert.DoesNotContain(secretNote, auditChanges, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW-TRACK-777", auditChanges, StringComparison.Ordinal);
    }

    // I05: AC-P2-07 (COD awaiting_settlement in same transaction + P2-07 deposit/overpayment reuse + outbox single handler)
    [Fact]
    public async Task AcP207DeliveredCodOrderCreatesAwaitingSettlementPaymentInSameTransactionAndProcessesOutbox()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        var deliveredLog = new List<string>();
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IOrderOutboxStep>(new RecordingDeliveredStep(deliveredLog));
        });
        await app.LoginAsync();

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 1_000_000L, suffix: "shp05");

        // Create COD order total 1,000,000 and record partial deposit 300,000 via P2-07 -> remaining = 700,000.
        var order = await CreateAdminOrderAsync(
            app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod,
            overrideShippingFeeVnd: 0L, confirmNow: true);

        using (var deposit = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = clock.UtcNow.AddMinutes(-1).ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, deposit.StatusCode);

        // Create shipment -> codAmountVnd = 700,000 (from OrderPaymentStatusCalculator).
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        var shippedAt = clock.UtcNow;
        using var createResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new
        {
            carrierName = "GHN",
            trackingCode = "GHN-COD-700K"
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var shipment = (await createResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
        Assert.Equal(700_000L, shipment.CodAmountVnd);

        // picked_up -> in_transit
        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var r1 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "picked_up",
            occurredAt = clock.UtcNow.AddMinutes(-10).ToString("O")
        }))
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var r2 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "in_transit",
            occurredAt = clock.UtcNow.AddMinutes(-5).ToString("O"),
            note = "Den kho HCM"
        }))
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // Invalid delivered_at (< shipped_at or > now) -> 422 invalid_delivered_at.
        using (var badPast = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "delivered",
            occurredAt = shippedAt.AddMinutes(-1).ToString("O")
        }))
            await AssertProblemAsync(badPast, HttpStatusCode.UnprocessableEntity, "invalid_delivered_at");

        using (var badFuture = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "delivered",
            occurredAt = clock.UtcNow.AddMinutes(5).ToString("O")
        }))
            await AssertProblemAsync(badFuture, HttpStatusCode.UnprocessableEntity, "invalid_delivered_at");

        // Valid delivered event -> order delivered + COD awaiting_settlement payment in SAME tx.
        clock.UtcNow = clock.UtcNow.AddHours(2);
        using var deliveredResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "delivered",
            occurredAt = clock.UtcNow.AddMinutes(-5).ToString("O"),
            note = "Khach da nhan hang"
        });
        Assert.Equal(HttpStatusCode.OK, deliveredResp.StatusCode);
        var deliveredDto = (await deliveredResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
        Assert.Equal(ShipmentStatus.Delivered, deliveredDto.Status);
        Assert.Equal(OrderStatus.Delivered, deliveredDto.OrderStatus);
        Assert.Equal(4, deliveredDto.Events.Count);

        // Verify sales.payments row (method='cod', provider='manual', amount_vnd=700_000, status='awaiting_settlement', confirmed_at IS NULL).
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}' AND method='cod' AND provider='manual' AND status='awaiting_settlement' AND amount_vnd=700000 AND confirmed_at IS NULL"));
        // Order payment_status stays partially_paid until P2-09 COD settlement confirms it.
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{order.Id}'"));

        // Interop with P2-07 GET /api/v1/admin/payments/awaiting?type=cod&carrier=GHN!
        var awaitingCod = await app.Client.GetFromJsonAsync<AwaitingPaymentsPage>("/api/v1/admin/payments/awaiting?type=cod&carrier=GHN", WebJson);
        Assert.NotNull(awaitingCod);
        var codItem = Assert.Single(awaitingCod.CodItems);
        Assert.Equal(order.Id, codItem.OrderId);
        Assert.Equal(700_000L, codItem.AmountVnd);
        Assert.Equal("GHN", codItem.CarrierName);
        Assert.Equal("GHN-COD-700K", codItem.TrackingCode);

        // Verify single CompositeOrderOutboxHandler processes OrderDelivered cleanly with custom IOrderOutboxStep.
        await app.ExecuteAsync("DELETE FROM platform.outbox_messages WHERE type NOT LIKE 'Order%';");
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.MessageType == OrderEventTypes.OrderDelivered).ToArray();
            Assert.Single(handlers);
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(50, CancellationToken.None);
        }
        Assert.Contains($"{order.Code}:{OrderStatus.Delivered}", deliveredLog);
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderDelivered}' AND status='processed' AND payload->>'orderId'='{order.Id}'"));
    }

    // I06: Bank transfer, 100%-deposited COD, and 0-VND COD order create ZERO COD payment rows on delivered
    [Fact]
    public async Task DeliveredBankTransferPrepaidCodAndZeroVndCodOrdersDoNotCreateCodPayment()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, suffix: "shp06");

        // 1. Bank transfer paid order.
        var bankOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L, confirmNow: true);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{bankOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = clock.UtcNow.ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        // 2. COD 100% pre-deposited order.
        var prepaidCod = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, overrideShippingFeeVnd: 0L, confirmNow: true);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{prepaidCod.Id}/payments", new
        {
            method = "cash",
            amountVnd = 500_000L,
            receivedAt = clock.UtcNow.ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        // 3. 0-VND COD order (100% owner manual discount, shipping = 0 -> total_vnd = 0 per O3a=C).
        var zeroVndCod = await CreateAdminOrderAsync(
            app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod,
            manualDiscountVnd: 500_000L, discountNote: "Tang khach VIP", overrideShippingFeeVnd: 0L, confirmNow: true);
        Assert.Equal(0L, zeroVndCod.TotalVnd);
        Assert.Equal(OrderPaymentStatus.Paid, zeroVndCod.PaymentStatus);

        foreach (var order in new[] { bankOrder, prepaidCod, zeroVndCod })
        {
            clock.UtcNow = clock.UtcNow.AddMinutes(10);
            using var createResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var shipment = (await createResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Equal(0L, shipment.CodAmountVnd);

            clock.UtcNow = clock.UtcNow.AddHours(1);
            using var delResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
            {
                status = "delivered",
                occurredAt = clock.UtcNow.ToString("O")
            });
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
            Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}' AND method='cod'"));
        }
    }

    // I06b: Concurrent delivered events produce exact 1 x 200, 0 duplicate COD payments, 0 duplicate OrderDelivered outbox events
    [Fact]
    public async Task ConcurrentDeliveredEventsCreateAtMostOneCodPaymentAndOneOrderDeliveredOutboxEvent()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 750_000L, suffix: "shp06b");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, overrideShippingFeeVnd: 0L, confirmNow: true);

        using var created = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipment = (await created.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        clock.UtcNow = clock.UtcNow.AddHours(2);
        var occurredAt = clock.UtcNow.AddMinutes(-1).ToString("O");

        const int workers = 8;
        var clients = new List<(HttpClient Client, string Csrf)>(workers);
        try
        {
            for (var i = 0; i < workers; i++)
            {
                var c = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(c, "owner@example.test", P009Harness.Password);
                var csrf = await GetCsrfTokenAsync(c);
                clients.Add((c, csrf));
            }

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = 0;
            var results = await Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref ready) == workers) startGate.TrySetResult();
                await startGate.Task;
                using var resp = await SendWithCsrfAsync(
                    clients[i].Client,
                    HttpMethod.Post,
                    $"/api/v1/admin/shipments/{shipment.Id}/events",
                    new { status = "delivered", occurredAt },
                    clients[i].Csrf);
                var body = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, body);
            })));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(workers - 1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.All(results.Where(r => r.StatusCode == HttpStatusCode.Conflict), r =>
            {
                using var doc = JsonDocument.Parse(r.body);
                Assert.Equal("invalid_status_transition", doc.RootElement.GetProperty("code").GetString());
            });

            Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}' AND method='cod' AND status='awaiting_settlement'"));
            Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderDelivered}' AND payload->>'orderId'='{order.Id}'"));
        }
        finally
        {
            foreach (var (c, _) in clients) c.Dispose();
        }
    }

    // I07: G16 & BR-SHP-03 & BR-INV-05 & O1=A payment_status recalc on failed -> returned restock
    [Fact]
    public async Task G16BrShp03FailedDoesNotRestockAndRecalculatesPaymentStatusThenReturnedRestocksOnceWithOrderRefTypeAndWeightedAverage()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        // Start with 4 units @ avg_cost = 400,000.
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 4, priceVnd: 1_000_000L, suffix: "shp07", avgCostVnd: 400_000L);

        // COD order qty=2 (total=2,000,000) with partial deposit 500,000 so delivery_failed activates NeedsRefund (O1=A/O3a=C).
        var order = await CreateAdminOrderAsync(
            app, customerId, variantId, quantity: 2, paymentMethod: OrderPaymentMethod.Cod,
            overrideShippingFeeVnd: 0L, confirmNow: true);
        using (var dep = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = clock.UtcNow.ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, dep.StatusCode);

        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        using var createResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var shipment = (await createResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
        Assert.Equal(1_500_000L, shipment.CodAmountVnd);
        // After ship: on_hand = 2, avg_cost = 400,000, locked line unit_cost = 400,000.
        Assert.Equal(2, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));

        // Passing returnCostVnd on non-returned status -> 400 validation_failed (GAP-P208-02 = A).
        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var badCostOnFailed = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Khach tu choi",
            returnCostVnd = 20_000L
        }))
            await AssertProblemAsync(badCostOnFailed, HttpStatusCode.BadRequest, "validation_failed");

        // Failed without note -> 422 failure_reason_required.
        using (var missingNote = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O")
        }))
            await AssertProblemAsync(missingNote, HttpStatusCode.UnprocessableEntity, "failure_reason_required");

        // Failed with note -> order delivery_failed, ZERO restock yet, payment_status preserved/recalculated for O1=A.
        using (var failedResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Khach bom hang 3 lan"
        }))
        {
            Assert.Equal(HttpStatusCode.OK, failedResp.StatusCode);
            var dto = (await failedResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Equal(ShipmentStatus.Failed, dto.Status);
            Assert.Equal(OrderStatus.DeliveryFailed, dto.OrderStatus);
        }

        Assert.Equal(2, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.stock_movements WHERE movement_type='return_restock' AND ref_id='{order.Id}'"));
        // Verify P2-07 cancellation refund works immediately on delivery_failed (confirming O1=A/O3b compatibility).
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        using (var cancelRefund = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "cancellation",
            amountVnd = 500_000L,
            method = "bank_transfer",
            reason = "Hoan coc do giao that bai",
            refundedAt = clock.UtcNow.ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, cancelRefund.StatusCode);
            var refundDto = (await cancelRefund.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.Refunded, refundDto.OrderPaymentStatus);
            Assert.False(refundDto.NeedsRefund);
        }

        // Receive 2 new units @ 700,000 while order goods are in return transit:
        // on_hand: 2 -> 4; avg_cost = (2*400k + 2*700k) / 4 = 550,000.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();
            var posted = await uow.ExecuteAsync(async ct =>
            {
                var locked = await store.LockAsync([variantId], ct);
                Assert.Null(InventoryMutation.Plan(locked[variantId], new("receipt", 2, 700_000L, "receipt", Guid.CreateVersion7(), null, "staff", app.OwnerId), out var posting));
                mutation.Stage([posting!]);
                return Result.Success(true);
            }, CancellationToken.None);
            Assert.True(posted.IsSuccess);
        }
        Assert.Equal(550_000L, await ScalarAsync<long>(app, $"SELECT avg_cost_vnd FROM inventory.stock_levels WHERE variant_id='{variantId}'"));

        // Now record 'returned' event with returnCostVnd = 18,000:
        // Restock 2 units @ locked line unit_cost_vnd = 400,000:
        // on_hand: 4 -> 6; new avg_cost = (4*550k + 2*400k) / 6 = 3,000,000 / 6 = 500,000!
        clock.UtcNow = clock.UtcNow.AddHours(2);
        using (var returnedResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "returned",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Hang nguyen ven da nhap kho",
            returnCostVnd = 18_000L
        }))
        {
            Assert.Equal(HttpStatusCode.OK, returnedResp.StatusCode);
            var dto = (await returnedResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
            Assert.Equal(ShipmentStatus.Returned, dto.Status);
            Assert.Equal(18_000L, dto.ReturnCostVnd);
            Assert.NotNull(dto.ReturnedAt);
        }

        Assert.Equal(6, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(500_000L, await ScalarAsync<long>(app, $"SELECT avg_cost_vnd FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.stock_movements WHERE movement_type='return_restock' AND ref_type='order' AND ref_id='{order.Id}' AND quantity=2 AND unit_cost_vnd=400000"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.stock_movements WHERE movement_type='return_restock' AND ref_type='return' AND ref_id='{order.Id}'"));
        Assert.Equal(0, await ScalarAsync<int>(app, $"SELECT returned_quantity FROM sales.order_lines WHERE id='{order.Lines[0].Id}'"));
    }

    // I08: Anti-double-restock (sequential + 8 concurrent returned requests via barrier)
    [Fact]
    public async Task ReturnedEventBlocksSecondCallAndConcurrentDuplicateNeverDoubleRestocks()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 500_000L, suffix: "shp08", avgCostVnd: 200_000L);
        var order = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 2, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);

        using var created = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
        var shipment = (await created.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var fail = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Khach khong nhan"
        }))
            Assert.Equal(HttpStatusCode.OK, fail.StatusCode);

        clock.UtcNow = clock.UtcNow.AddHours(2);
        var returnedAt = clock.UtcNow.ToString("O");

        const int workers = 8;
        var clients = new List<(HttpClient Client, string Csrf)>(workers);
        try
        {
            for (var i = 0; i < workers; i++)
            {
                var c = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(c, "owner@example.test", P009Harness.Password);
                var csrf = await GetCsrfTokenAsync(c);
                clients.Add((c, csrf));
            }

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = 0;
            var results = await Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref ready) == workers) startGate.TrySetResult();
                await startGate.Task;
                using var resp = await SendWithCsrfAsync(
                    clients[i].Client,
                    HttpMethod.Post,
                    $"/api/v1/admin/shipments/{shipment.Id}/events",
                    new { status = "returned", occurredAt = returnedAt, returnCostVnd = 15_000L },
                    clients[i].Csrf);
                var body = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, body);
            })));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(workers - 1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var (c, _) in clients) c.Dispose();
        }

        // Sequential second call -> 409 invalid_status_transition.
        using (var second = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "returned",
            occurredAt = clock.UtcNow.ToString("O")
        }))
            await AssertProblemAsync(second, HttpStatusCode.Conflict, "invalid_status_transition");

        Assert.Equal(5, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantId}'"));
        Assert.Equal(5L, await ScalarAsync<long>(app, $"SELECT coalesce(sum(quantity), 0) FROM inventory.stock_movements WHERE variant_id='{variantId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM inventory.stock_movements WHERE movement_type='return_restock' AND ref_type='order' AND ref_id='{order.Id}'"));
    }

    // I09: BR-SEO-21 full HTTP lifecycle on unique piece: confirmed (out_of_stock) -> shipped (sold) -> failed (sold) -> returned (in_stock) -> resold!
    [Fact]
    public async Task BrSeo21UniquePieceShippedBecomesSoldFailedStaysSoldReturnedRestoresInStockAndClearsSoldOutAt()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, productId, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: true, onHand: 1, priceVnd: 5_500_000L, suffix: "seo21", avgCostVnd: 2_500_000L);

        // 1. Confirmed -> Store API returns out_of_stock, sold_out_at IS NULL.
        var order1 = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 1, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        var confirmedDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-seo21", WebJson);
        Assert.Equal("out_of_stock", Assert.Single(confirmedDetail!.Variants).Availability);
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));

        // 2. POST .../shipments -> Store API list + detail return "sold", sold_out_at IS NOT NULL, StorefrontInvalidate emitted!
        await app.ExecuteAsync("DELETE FROM platform.outbox_messages;");
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        using var shipResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order1.Id}/shipments", new { carrierName = "GHN" });
        Assert.Equal(HttpStatusCode.Created, shipResp.StatusCode);
        var shipment1 = (await shipResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        var soldDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-seo21", WebJson);
        Assert.Equal("sold", Assert.Single(soldDetail!.Variants).Availability);
        var soldPage = await app.Client.GetFromJsonAsync<StoreProductPage>("/api/v1/store/products?q=seo21", WebJson);
        Assert.Equal("sold", Assert.Single(soldPage!.Items).Availability);
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NOT NULL FROM catalog.products WHERE id='{productId}'"));
        var shipInv = await ScalarAsync<string>(app, $"SELECT coalesce(string_agg(payload::text, ' '), '') FROM platform.outbox_messages WHERE type='{StorefrontInvalidate.Message}'");
        Assert.Contains("product:pot-p2-seo21", shipInv, StringComparison.Ordinal);

        // 3. POST .../events failed -> stays "sold" and sold_out_at IS NOT NULL!
        clock.UtcNow = clock.UtcNow.AddHours(2);
        using (var failResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment1.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Khach khong nhan"
        }))
            Assert.Equal(HttpStatusCode.OK, failResp.StatusCode);

        var failedDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-seo21", WebJson);
        Assert.Equal("sold", Assert.Single(failedDetail!.Variants).Availability);
        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NOT NULL FROM catalog.products WHERE id='{productId}'"));

        // 4. POST .../events returned -> clears products.sold_out_at = NULL, emits StorefrontInvalidate, Store API list + detail return "in_stock"!
        await app.ExecuteAsync("DELETE FROM platform.outbox_messages;");
        clock.UtcNow = clock.UtcNow.AddHours(2);
        using (var returnResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment1.Id}/events", new
        {
            status = "returned",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Da nhap lai am doc ban"
        }))
            Assert.Equal(HttpStatusCode.OK, returnResp.StatusCode);

        Assert.True(await ScalarAsync<bool>(app, $"SELECT sold_out_at IS NULL FROM catalog.products WHERE id='{productId}'"));
        var inStockDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-seo21", WebJson);
        Assert.Equal("in_stock", Assert.Single(inStockDetail!.Variants).Availability);
        var inStockPage = await app.Client.GetFromJsonAsync<StoreProductPage>("/api/v1/store/products?q=seo21", WebJson);
        Assert.Equal("in_stock", Assert.Single(inStockPage!.Items).Availability);
        var returnInv = await ScalarAsync<string>(app, $"SELECT coalesce(string_agg(payload::text, ' '), '') FROM platform.outbox_messages WHERE type='{StorefrontInvalidate.Message}'");
        Assert.Contains("product:pot-p2-seo21", returnInv, StringComparison.Ordinal);

        // 5. Unique piece can be re-ordered and re-shipped via HTTP!
        var order2 = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 1, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);
        clock.UtcNow = clock.UtcNow.AddMinutes(15);
        using var ship2 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order2.Id}/shipments", new { carrierName = "GHTK" });
        Assert.Equal(HttpStatusCode.Created, ship2.StatusCode);
        var resoldDetail = await app.Client.GetFromJsonAsync<StoreProductDetail>("/api/v1/store/products/pot-p2-seo21", WebJson);
        Assert.Equal("sold", Assert.Single(resoldDetail!.Variants).Availability);
    }

    // I10: delivery_failed is terminal (BR-ORD-03); cannot create 2nd shipment on same order
    [Fact]
    public async Task AfterFailedShipmentOrderCannotCreateSecondShipmentBecauseDeliveryFailedIsTerminal()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 5, priceVnd: 500_000L, suffix: "shp10");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);

        using var s1Resp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
        var s1 = (await s1Resp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var failResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{s1.Id}/events", new
        {
            status = "failed",
            occurredAt = clock.UtcNow.ToString("O"),
            note = "Khach khong nhan"
        }))
            Assert.Equal(HttpStatusCode.OK, failResp.StatusCode);

        // Even though ux_shipment_active_order is now free, order is terminal delivery_failed -> 409 invalid_status_transition.
        using var secondShip = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHTK" });
        await AssertProblemAsync(secondShip, HttpStatusCode.Conflict, "invalid_status_transition");
    }

    // I11: Security, permissions (owner & sales allowed, content 403), HTTPS 400, unauth 401, CSRF 400, 415, offset-less timestamp 400
    [Fact]
    public async Task SecurityAndPermissionsEnforceOwnerAndSalesWriteBlockContentCsrfAndUnauthenticated()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var fakeOrderId = Guid.CreateVersion7();
        var fakeShipmentId = Guid.CreateVersion7();

        // 1. Plain HTTP -> 400 https_required.
        using (var httpClient = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("http://localhost"),
            AllowAutoRedirect = false
        }))
        {
            using var r = await httpClient.PostAsJsonAsync($"/api/v1/admin/orders/{fakeOrderId}/shipments", new { carrierName = "GHN" });
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "https_required");
        }

        // 2. Unauthenticated HTTPS -> 401 unauthenticated on all 3 endpoints.
        using (var r1 = await app.Client.PostAsJsonAsync($"/api/v1/admin/orders/{fakeOrderId}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(r1, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var r2 = await app.Client.PatchAsJsonAsync($"/api/v1/admin/shipments/{fakeShipmentId}", new { shippingCostVnd = 1000 }))
            await AssertProblemAsync(r2, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var r3 = await app.Client.PostAsJsonAsync($"/api/v1/admin/shipments/{fakeShipmentId}/events", new { status = "picked_up", occurredAt = "2026-11-26T06:00:00Z" }))
            await AssertProblemAsync(r3, HttpStatusCode.Unauthorized, "unauthenticated");

        // 3. Authenticated owner without CSRF -> 400 csrf_invalid on all 3 endpoints.
        await app.LoginAsync();
        using (var r1 = await app.Client.PostAsJsonAsync($"/api/v1/admin/orders/{fakeOrderId}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(r1, HttpStatusCode.BadRequest, "csrf_invalid");
        using (var r2 = await app.Client.PatchAsJsonAsync($"/api/v1/admin/shipments/{fakeShipmentId}", new { shippingCostVnd = 1000 }))
            await AssertProblemAsync(r2, HttpStatusCode.BadRequest, "csrf_invalid");
        using (var r3 = await app.Client.PostAsJsonAsync($"/api/v1/admin/shipments/{fakeShipmentId}/events", new { status = "picked_up", occurredAt = "2026-11-26T06:00:00Z" }))
            await AssertProblemAsync(r3, HttpStatusCode.BadRequest, "csrf_invalid");

        // 4. Wrong Content-Type -> 415 unsupported_media_type on all 3 endpoints.
        var csrf = await GetCsrfTokenAsync(app.Client);
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, $"/api/v1/admin/orders/{fakeOrderId}/shipments"),
            (HttpMethod.Patch, $"/api/v1/admin/shipments/{fakeShipmentId}"),
            (HttpMethod.Post, $"/api/v1/admin/shipments/{fakeShipmentId}/events")
        })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("plain text", Encoding.UTF8, "text/plain")
            };
            req.Headers.Add("X-CSRF-TOKEN", csrf);
            using var resp = await app.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }

        // Seed roles & order.
        var salesEmail = await CreateStaffAsync(app, "sales");
        var contentEmail = await CreateStaffAsync(app, "content");
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 10, priceVnd: 500_000L, suffix: "shp11");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, confirmNow: true);

        // 5. Sales CAN create, patch, and record events (orders.write).
        await app.LoginAsync(salesEmail, StaffNewPassword);
        using var salesCreate = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
        Assert.Equal(HttpStatusCode.Created, salesCreate.StatusCode);
        var shipment = (await salesCreate.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;

        using (var salesPatch = await app.MutateAsync(HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", new { shippingCostVnd = 30_000L }))
            Assert.Equal(HttpStatusCode.OK, salesPatch.StatusCode);

        // Reject offset-less occurredAt with 400 validation_failed.
        using (var noOffset = await SendRawJsonWithCsrfAsync(app.Client, HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", """{"status":"picked_up","occurredAt":"2026-11-26T06:10:00"}"""))
            await AssertProblemAsync(noOffset, HttpStatusCode.BadRequest, "validation_failed");

        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        using (var salesEvent = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new
        {
            status = "picked_up",
            occurredAt = clock.UtcNow.ToString("O")
        }))
            Assert.Equal(HttpStatusCode.OK, salesEvent.StatusCode);

        // 6. Content role gets 403 forbidden on all 3 endpoints.
        await app.LoginAsync(contentEmail, StaffNewPassword);
        using (var c1 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" }))
            await AssertProblemAsync(c1, HttpStatusCode.Forbidden, "forbidden");
        using (var c2 = await app.MutateAsync(HttpMethod.Patch, $"/api/v1/admin/shipments/{shipment.Id}", new { shippingCostVnd = 1000 }))
            await AssertProblemAsync(c2, HttpStatusCode.Forbidden, "forbidden");
        using (var c3 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{shipment.Id}/events", new { status = "in_transit", occurredAt = clock.UtcNow.ToString("O") }))
            await AssertProblemAsync(c3, HttpStatusCode.Forbidden, "forbidden");
    }

    // I12: Lock order concurrency (L1 -> L1b -> L2 -> L3 -> L4) zero 40P01 deadlocks and 0 ReconcileInventory mismatches
    [Fact]
    public async Task LockOrderConcurrencyCreateShipmentAndReturnedDoNotDeadlockWithCatalogOrMultiVariantOrders()
    {
        var clock = new AdjustableClock(new(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await using var app = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await app.LoginAsync();

        var (customerId, productA, variantA, _) = await SeedProductVariantAndCustomerAsync(
            app, isUniquePiece: false, onHand: 50, priceVnd: 400_000L, suffix: "lka", avgCostVnd: 180_000L);
        var variantB = await AddProductAndVariantAsync(app, isUniquePiece: false, onHand: 50, priceVnd: 600_000L, suffix: "lkb", avgCostVnd: 260_000L);

        // Prepare 4 confirmed multi-variant orders (for concurrent ship) and 4 failed shipments (for concurrent return restock).
        var ordersToShip = new List<Guid>(4);
        var shipmentsToReturn = new List<Guid>(4);

        for (var i = 0; i < 8; i++)
        {
            OrderLineRequest[] lines = i % 2 == 0
                ? [new(variantA, 1), new(variantB, 2)]
                : [new(variantB, 2), new(variantA, 1)];
            OrderSummary order;
            await using (var scope = app.Factory.Services.CreateAsyncScope())
            {
                var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>()
                    .Handle(new CreateAdminOrder(
                        SalesChannel.ZaloPersonal, customerId, app.OwnerId, true, OrderPaymentMethod.Cod,
                        SampleContact, lines, null, null, null, null, 0L, SampleAttribution, null, null,
                        ConfirmNow: true, CodCallConfirmed: true, ConfirmNote: "OK"), CancellationToken.None);
                Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
                order = res.Value;
            }

            if (i < 4)
            {
                ordersToShip.Add(order.Id);
            }
            else
            {
                using var sResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/shipments", new { carrierName = "GHN" });
                Assert.Equal(HttpStatusCode.Created, sResp.StatusCode);
                var s = (await sResp.Content.ReadFromJsonAsync<ShipmentDetailView>(WebJson))!;
                clock.UtcNow = clock.UtcNow.AddMinutes(5);
                using var fResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/shipments/{s.Id}/events", new
                {
                    status = "failed",
                    occurredAt = clock.UtcNow.ToString("O"),
                    note = "Hoan ve"
                });
                Assert.Equal(HttpStatusCode.OK, fResp.StatusCode);
                shipmentsToReturn.Add(s.Id);
            }
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        var occurredAt = clock.UtcNow;
        var errors = new ConcurrentBag<Exception>();
        var totalTasks = ordersToShip.Count + shipmentsToReturn.Count + 4;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        var tasks = new List<Task>(totalTasks);
        foreach (var orderId in ordersToShip)
        {
            var captured = orderId;
            tasks.Add(Task.Run(async () =>
            {
                await using var scope = app.Factory.Services.CreateAsyncScope();
                if (Interlocked.Increment(ref ready) == totalTasks) startGate.TrySetResult();
                await startGate.Task;
                try
                {
                    var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateOrderShipment, ShipmentDetailView>>()
                        .Handle(new CreateOrderShipment(captured, "GHN", null, null, null, app.OwnerId), CancellationToken.None);
                    Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
                }
                catch (Exception ex) { errors.Add(ex); }
            }));
        }

        foreach (var shipmentId in shipmentsToReturn)
        {
            var captured = shipmentId;
            tasks.Add(Task.Run(async () =>
            {
                await using var scope = app.Factory.Services.CreateAsyncScope();
                if (Interlocked.Increment(ref ready) == totalTasks) startGate.TrySetResult();
                await startGate.Task;
                try
                {
                    var res = await scope.ServiceProvider.GetRequiredService<ICommandHandler<RecordShipmentEvent, ShipmentDetailView>>()
                        .Handle(new RecordShipmentEvent(captured, ShipmentStatus.Returned, occurredAt, "Nhap lai", null, app.OwnerId), CancellationToken.None);
                    Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
                }
                catch (Exception ex) { errors.Add(ex); }
            }));
        }

        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await using var scope = app.Factory.Services.CreateAsyncScope();
                if (Interlocked.Increment(ref ready) == totalTasks) startGate.TrySetResult();
                await startGate.Task;
                try
                {
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var productStore = scope.ServiceProvider.GetRequiredService<IProductStore>();
                    var res = await uow.ExecuteAsync(async ct =>
                    {
                        var loaded = await productStore.LoadAsync(productA, update: true, ct);
                        Assert.NotNull(loaded);
                        return Result.Success(true);
                    }, CancellationToken.None);
                    Assert.True(res.IsSuccess);
                }
                catch (Exception ex) { errors.Add(ex); }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Empty(errors);

        // Stock levels match stock_movements ledger and ReconcileInventory reports 0 mismatches!
        Assert.Equal(46, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantA}'"));
        Assert.Equal(42, await ScalarAsync<int>(app, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id='{variantB}'"));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var alerts = await scope.ServiceProvider.GetRequiredService<IInventoryQueries>()
                .AlertsAsync(new GetInventoryAlerts(50), CancellationToken.None);
            Assert.Equal(0L, alerts.MismatchTotal);
            Assert.Empty(alerts.Mismatches);
        }

        var reconcileConfig = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = app.RuntimeConnection }).Build();
        using var reconcileJob = new ReconcileInventory(
            app.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            reconcileConfig,
            TimeProvider.System,
            clock,
            NullLogger<ReconcileInventory>.Instance);
        Assert.True(await reconcileJob.RunOnceAsync());
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{InventoryAlert.Message}' AND payload->>'kind'='{InventoryAlert.ReconciliationMismatch}'"));
    }
}
