using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Payments;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class PaymentRecordingAndIdempotencyTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    internal static readonly OrderContactSnapshot SampleContact = new(
        "Nguyen Van A", "+84900000701", "buyer701@example.invalid",
        "Nguyen Van A", "+84900000701", "79", "TP Ho Chi Minh", "79001", "Phuong 1", "10 Duong Thu Nghiem");
    internal static readonly OrderAttributionSnapshot SampleAttribution = new(OrderAcquisitionSource.Direct, true);

    internal static async Task<string> GetCsrfTokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

    internal static async Task<HttpResponseMessage> PostIdempotentJsonAsync(
        HttpClient client,
        string path,
        object body,
        string? idempotencyKey,
        string? csrfToken = null)
    {
        var token = csrfToken ?? await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    internal static async Task<HttpResponseMessage> PostRawJsonAsync(
        HttpClient client,
        string path,
        string rawJson,
        string? idempotencyKey = null,
        string? csrfToken = null)
    {
        var token = csrfToken ?? await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    internal static async Task<OrderSummary> CreateWebBankOrderAsync(
        P009Harness app,
        Guid customerId,
        Guid variantId,
        int quantity = 1,
        string paymentMethod = OrderPaymentMethod.BankTransfer,
        string? voucherCode = null)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWebOrder, OrderSummary>>();
        var res = await handler.Handle(new CreateWebOrder(
            customerId,
            null,
            paymentMethod,
            SampleContact,
            [new(variantId, quantity)],
            voucherCode,
            null,
            null,
            SampleAttribution,
            null), CancellationToken.None);
        Assert.True(res.IsSuccess, res.IsSuccess ? "" : $"{res.Error.Code}: {res.Error.Message}");
        return res.Value;
    }

    internal static async Task<OrderSummary> CreateAdminOrderAsync(
        P009Harness app,
        Guid customerId,
        Guid variantId,
        int quantity = 1,
        string paymentMethod = OrderPaymentMethod.BankTransfer,
        long? manualDiscountVnd = null,
        string? discountNote = null,
        long? overrideShippingFeeVnd = 0L,
        bool confirmNow = false)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
        var res = await handler.Handle(new CreateAdminOrder(
            SalesChannel.ZaloPersonal,
            customerId,
            app.OwnerId,
            true,
            paymentMethod,
            SampleContact,
            [new(variantId, quantity)],
            null,
            null,
            manualDiscountVnd,
            discountNote,
            overrideShippingFeeVnd,
            SampleAttribution,
            null,
            null,
            ConfirmNow: confirmNow,
            CodCallConfirmed: confirmNow,
            ConfirmNote: confirmNow ? "Da xac nhan" : null), CancellationToken.None);
        Assert.True(res.IsSuccess, res.IsSuccess ? "" : $"{res.Error.Code}: {res.Error.Message}");
        return res.Value;
    }

    [Fact]
    public async Task AcP204RecordPaymentSameIdempotencyKeyReplaysSingleRowAndDifferentPayloadReturns409()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "p1");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        var key = Guid.CreateVersion7().ToString("D");
        var path = $"/api/v1/admin/orders/{order.Id}/payments";
        var payload = new
        {
            method = "bank_transfer",
            amountVnd = 200_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-IDEM-001",
            note = "Chuyen khoan lan 1"
        };

        // Missing / malformed Idempotency-Key -> 400 validation_failed.
        using (var missingKey = await PostIdempotentJsonAsync(app.Client, path, payload, idempotencyKey: null))
            await AssertProblemAsync(missingKey, HttpStatusCode.BadRequest, "validation_failed");
        using (var badKey = await PostIdempotentJsonAsync(app.Client, path, payload, idempotencyKey: "not-a-uuid"))
            await AssertProblemAsync(badKey, HttpStatusCode.BadRequest, "validation_failed");

        // First call succeeds -> 1 payment row.
        using var first = await PostIdempotentJsonAsync(app.Client, path, payload, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstRaw = await first.Content.ReadAsStringAsync();
        var firstDto = JsonSerializer.Deserialize<PaymentRecordedView>(firstRaw, WebJson)!;
        Assert.Equal(200_000L, firstDto.AmountVnd);
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, firstDto.OrderPaymentStatus);
        Assert.Equal(200_000L, firstDto.PaidVnd);
        Assert.Equal(200_000L, firstDto.NetPaidVnd);
        Assert.False(firstDto.IsOverpaid);
        Assert.False(firstDto.NeedsRefund);

        // Replay with identical Idempotency-Key + identical payload -> 200 exact byte match, still 1 payment row.
        using var replay = await PostIdempotentJsonAsync(app.Client, path, payload, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstRaw, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}'"));

        // Same key with different amount -> 409 idempotency_key_reused.
        var differentAmount = new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-IDEM-001",
            note = "Chuyen khoan lan 1"
        };
        using var conflict = await PostIdempotentJsonAsync(app.Client, path, differentAmount, key);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}'"));
    }

    [Fact]
    public async Task AcP204TwentyConcurrentIdenticalIdempotentPaymentRequestsCreateExactlyOnePayment()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "p2");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        const int concurrency = 20;
        var key = Guid.CreateVersion7().ToString("D");
        var csrf = await GetCsrfTokenAsync(app.Client);
        var path = $"/api/v1/admin/orders/{order.Id}/payments";
        var payload = new
        {
            method = "bank_transfer",
            amountVnd = 250_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-CONC-IDEM-20"
        };

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref ready) == concurrency)
                startGate.TrySetResult();
            await startGate.Task;

            using var resp = await PostIdempotentJsonAsync(app.Client, path, payload, key, csrf);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, body);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var expectedBody = results[0].body;
        Assert.All(results, r => Assert.Equal(expectedBody, r.body));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{order.Id}'"));
    }

    [Fact]
    public async Task BrPay01DuplicateBankReferenceReturns409ViaSavepointThenAfterVoidAllowsReuse200()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "p3");
        var orderA = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        var orderB = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        // 1. Concurrent race across two distinct orders with identical bankReference -> SAVEPOINT handles 23505 cleanly.
        var csrf = await GetCsrfTokenAsync(app.Client);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var raceTasks = new[] { orderA.Id, orderB.Id }.Select(orderId => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref ready) == 2)
                startGate.TrySetResult();
            await startGate.Task;

            using var resp = await PostIdempotentJsonAsync(
                app.Client,
                $"/api/v1/admin/orders/{orderId}/payments",
                new
                {
                    method = "bank_transfer",
                    amountVnd = 300_000L,
                    receivedAt = "2026-11-25T05:00:00Z",
                    bankReference = "FT2611259999"
                },
                Guid.CreateVersion7().ToString("D"),
                csrf);
            return (orderId, resp.StatusCode, body: await resp.Content.ReadAsStringAsync());
        })).ToArray();

        var raceResults = await Task.WhenAll(raceTasks);
        var winner = Assert.Single(raceResults, r => r.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(raceResults, r => r.StatusCode == HttpStatusCode.Conflict);
        using (var loserDoc = JsonDocument.Parse(loser.body))
            Assert.Equal("duplicate_bank_reference", loserDoc.RootElement.GetProperty("code").GetString());

        var winnerDto = JsonSerializer.Deserialize<PaymentRecordedView>(winner.body, WebJson)!;

        // 2. Sequential duplicate also returns 409 and does not poison the failed idempotency key.
        var retryableKey = Guid.CreateVersion7().ToString("D");
        using (var dup = await PostIdempotentJsonAsync(
            app.Client,
            $"/api/v1/admin/orders/{loser.orderId}/payments",
            new
            {
                method = "bank_transfer",
                amountVnd = 300_000L,
                receivedAt = "2026-11-25T05:00:00Z",
                bankReference = "FT2611259999"
            },
            retryableKey,
            csrf))
        {
            await AssertProblemAsync(dup, HttpStatusCode.Conflict, "duplicate_bank_reference");
        }

        // 3. Owner voids the first payment -> partial unique index releases FT2611259999 -> recording again succeeds 200.
        using (var voidResp = await app.MutateAsync(
            HttpMethod.Post,
            $"/api/v1/admin/payments/{winnerDto.PaymentId}/void",
            new { reason = "Ghi nham don hang" }))
        {
            Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        }

        using var reused = await PostIdempotentJsonAsync(
            app.Client,
            $"/api/v1/admin/orders/{loser.orderId}/payments",
            new
            {
                method = "bank_transfer",
                amountVnd = 300_000L,
                receivedAt = "2026-11-25T05:00:00Z",
                bankReference = "FT2611259999"
            },
            retryableKey,
            csrf);
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);

        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.payments WHERE bank_reference='FT2611259999' AND status='voided'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "SELECT count(*) FROM sales.payments WHERE bank_reference='FT2611259999' AND status='confirmed'"));
    }

    [Fact]
    public async Task BrPay01PaymentValidationRejectsCodMethodZeroAmountFutureReceivedAtAndBackfilledOrder()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "p4");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        var path = $"/api/v1/admin/orders/{order.Id}/payments";

        // method = cod -> 400 validation_failed.
        using (var codResp = await PostIdempotentJsonAsync(app.Client, path, new
        {
            method = "cod",
            amountVnd = 100_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            await AssertProblemAsync(codResp, HttpStatusCode.BadRequest, "validation_failed");
        }

        // amountVnd <= 0 -> 400 validation_failed.
        using (var zeroResp = await PostIdempotentJsonAsync(app.Client, path, new
        {
            method = "cash",
            amountVnd = 0L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            await AssertProblemAsync(zeroResp, HttpStatusCode.BadRequest, "validation_failed");
        }

        // Missing ISO timezone offset on receivedAt -> 400 validation_failed.
        using (var noOffset = await PostRawJsonAsync(
            app.Client,
            path,
            """{"method":"cash","amountVnd":100000,"receivedAt":"2026-11-25T05:00:00"}""",
            Guid.CreateVersion7().ToString("D")))
        {
            await AssertProblemAsync(noOffset, HttpStatusCode.BadRequest, "validation_failed");
        }

        // Future receivedAt -> 422 received_at_in_future.
        using (var futureResp = await PostIdempotentJsonAsync(app.Client, path, new
        {
            method = "cash",
            amountVnd = 100_000L,
            receivedAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O")
        }, Guid.CreateVersion7().ToString("D")))
        {
            await AssertProblemAsync(futureResp, HttpStatusCode.UnprocessableEntity, "received_at_in_future");
        }

        // Cash payment without bankReference succeeds 200.
        using (var cashResp = await PostIdempotentJsonAsync(app.Client, path, new
        {
            method = "cash",
            amountVnd = 100_000L,
            receivedAt = "2026-11-25T05:00:00+07:00"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, cashResp.StatusCode);
            var dto = (await cashResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal("cash", dto.Method);
            Assert.Equal(100_000L, dto.PaidVnd);
        }

        // Backfilled historical order -> 422 backfilled_order_immutable.
        var backfilledOrder = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        await app.ExecuteAsync($"UPDATE sales.orders SET is_backfilled = true WHERE id = '{backfilledOrder.Id}'");
        using (var backfilledResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{backfilledOrder.Id}/payments", new
        {
            method = "cash",
            amountVnd = 100_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            await AssertProblemAsync(backfilledResp, HttpStatusCode.UnprocessableEntity, "backfilled_order_immutable");
        }
    }

    [Fact]
    public async Task BrPay03WebBankTransferReachingPaidAutoConfirmsWhenSettingTrueAndStaysPendingOtherwise()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 470_000L, suffix: "p5");

        // (a) Web bank_transfer order (470k + 30k default shipping = 500k) with setting=true.
        var webBankOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        Assert.Equal(500_000L, webBankOrder.TotalVnd);
        Assert.NotNull(webBankOrder.PaymentDueAt);

        // Partial 200k -> partially_paid, still pending.
        using (var part1 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webBankOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 200_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-AUTO-1"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, part1.StatusCode);
            var dto1 = (await part1.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto1.OrderStatus);
            Assert.Equal(OrderPaymentStatus.PartiallyPaid, dto1.OrderPaymentStatus);
            Assert.False(dto1.AutoConfirmed);
        }

        // Second 300k -> reaches paid -> auto-confirms via system actor!
        using (var part2 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webBankOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = "2026-11-25T05:05:00Z",
            bankReference = "FT-AUTO-2"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, part2.StatusCode);
            var dto2 = (await part2.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Confirmed, dto2.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto2.OrderPaymentStatus);
            Assert.True(dto2.AutoConfirmed);
        }

        await using (var db = app.OwnerContext())
        {
            var persistedOrder = await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == webBankOrder.Id);
            Assert.Equal(OrderStatus.Confirmed, persistedOrder.Status);
            Assert.NotNull(persistedOrder.ConfirmedAt);
            Assert.NotNull(persistedOrder.PaymentDueAt); // R-P207-03: ck_order_due intact.

            var history = await db.Set<OrderStatusHistory>().AsNoTracking()
                .Where(x => x.OrderId == webBankOrder.Id && x.ToStatus == OrderStatus.Confirmed)
                .SingleAsync();
            Assert.Equal(OrderStatus.Pending, history.FromStatus);
            Assert.Equal(OrderActorType.System, history.ActorType);
            Assert.Null(history.ActorId);
            Assert.Equal(OrderConfirmReasonCodes.AutoConfirmedOnFullPayment, history.Reason);

            var outbox = await db.OutboxMessages.AsNoTracking()
                .Where(x => x.Type == OrderEventTypes.OrderConfirmed)
                .SingleAsync();
            Assert.True(OrderOutboxMessage.TryParse(outbox.Payload, out var parsed));
            Assert.Equal(webBankOrder.Id, parsed.OrderId);
            Assert.Equal(OrderConfirmReasonCodes.AutoConfirmedOnFullPayment, parsed.ReasonCode);
        }

        // Process outbox batch -> OrderConfirmed processes cleanly with 0 dead messages.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            var processed = await processor.ProcessAsync(50, CancellationToken.None);
            Assert.True(processed >= 1);
        }
        Assert.Equal(0L, await ScalarAsync<long>(app, "SELECT count(*) FROM platform.outbox_messages WHERE status = 'dead'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type = '{OrderEventTypes.OrderConfirmed}' AND status = 'processed'"));

        // (b) Admin bank_transfer pending order -> reaches paid but stays pending.
        var adminBankOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);
        using (var adminPaid = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{adminBankOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = adminBankOrder.TotalVnd,
            receivedAt = "2026-11-25T05:10:00Z",
            bankReference = "FT-ADMIN-1"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, adminPaid.StatusCode);
            var dto = (await adminPaid.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.False(dto.AutoConfirmed);
        }

        // (c) Web COD pending order -> reaches paid but stays pending.
        var webCodOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod);
        using (var codPaid = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webCodOrder.Id}/payments", new
        {
            method = "cash",
            amountVnd = webCodOrder.TotalVnd,
            receivedAt = "2026-11-25T05:12:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, codPaid.StatusCode);
            var dto = (await codPaid.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.False(dto.AutoConfirmed);
        }

        // (d) Setting orders.auto_confirm_on_full_payment = false -> web bank_transfer reaches paid but stays pending.
        await app.ExecuteAsync("UPDATE platform.settings SET value = 'false' WHERE key = 'orders.auto_confirm_on_full_payment'");
        var webNoAutoOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        using (var noAutoPaid = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webNoAutoOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = webNoAutoOrder.TotalVnd,
            receivedAt = "2026-11-25T05:15:00Z",
            bankReference = "FT-NO-AUTO"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, noAutoPaid.StatusCode);
            var dto = (await noAutoPaid.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.False(dto.AutoConfirmed);
        }
    }

    [Fact]
    public async Task BrPay01RecordPaymentOnCancelledOrderSucceedsUpdatesPaymentStatusAndSetsNeedsRefund()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "p6");
        var order = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            var cancelled = await cancel.Handle(new CancelOrder(order.Id, order.Version, OrderActorType.Staff, app.OwnerId, "Khach bao huy"), CancellationToken.None);
            Assert.True(cancelled.IsSuccess);
        }

        using var resp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = "2026-11-25T05:20:00Z",
            bankReference = "FT-CANCELLED-LATE"
        }, Guid.CreateVersion7().ToString("D"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        Assert.Equal(OrderStatus.Cancelled, dto.OrderStatus);
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, dto.OrderPaymentStatus);
        Assert.Equal(300_000L, dto.NetPaidVnd);
        Assert.False(dto.IsOverpaid);
        Assert.True(dto.NeedsRefund);
        Assert.False(dto.AutoConfirmed);
    }

    [Fact]
    public async Task BrPay01BrShp03ZeroVndOrderAndTerminalTransitionsRecalculatePaymentStatusAndExcludeZeroVndFromAwaiting()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 500_000L, suffix: "z0");

        // 1. 0-VND admin bank_transfer order (100% manual discount, 0 shipping): starts 'paid', 'pending', 0 payments, excluded from awaiting.
        var zeroOrder = await CreateAdminOrderAsync(
            app, customerId, variantId,
            paymentMethod: OrderPaymentMethod.BankTransfer,
            manualDiscountVnd: 500_000L,
            discountNote: "Mien phi 100%",
            overrideShippingFeeVnd: 0L,
            confirmNow: false);
        Assert.Equal(0L, zeroOrder.TotalVnd);
        Assert.Equal(OrderStatus.Pending, zeroOrder.Status);
        Assert.Equal(OrderPaymentStatus.Paid, zeroOrder.PaymentStatus);
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{zeroOrder.Id}'"));

        using (var awaitingZero = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer"))
        {
            Assert.Equal(HttpStatusCode.OK, awaitingZero.StatusCode);
            var page = (await awaitingZero.Content.ReadFromJsonAsync<AwaitingPaymentsPage>(WebJson))!;
            Assert.DoesNotContain(page.BankTransferItems, x => x.OrderId == zeroOrder.Id);
        }

        // Cancelling the 0-VND order recalculates payment_status -> 'unpaid' in the same transaction (O3a=C Branch 2).
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            var cancelled = await cancel.Handle(new CancelOrder(zeroOrder.Id, zeroOrder.Version, OrderActorType.Staff, app.OwnerId, "Huy don 0d"), CancellationToken.None);
            Assert.True(cancelled.IsSuccess);
            Assert.Equal(OrderStatus.Cancelled, cancelled.Value.Status);
            Assert.Equal(OrderPaymentStatus.Unpaid, cancelled.Value.PaymentStatus);
        }

        // 2. Overpaid + overpayment refunded order ('paid') -> CancelOrder recalculates payment_status -> 'partially_refunded'!
        var cancelOverpayOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{cancelOverpayOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 700_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        using (var ovp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{cancelOverpayOrder.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan thu thua",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ovp.StatusCode);
            var dto = (await ovp.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
        }

        uint cancelVer;
        await using (var db = app.OwnerContext())
            cancelVer = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == cancelOverpayOrder.Id)).Version;

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            var cancelled = await cancel.Handle(new CancelOrder(cancelOverpayOrder.Id, cancelVer, OrderActorType.Staff, app.OwnerId, "Khach huy sau hoan thua"), CancellationToken.None);
            Assert.True(cancelled.IsSuccess);
            Assert.Equal(OrderStatus.Cancelled, cancelled.Value.Status);
            Assert.Equal(OrderPaymentStatus.PartiallyRefunded, cancelled.Value.PaymentStatus);
        }

        // 3. Overpaid + overpayment refunded order -> shipping -> MarkOrderDeliveryFailed recalculates payment_status -> 'partially_refunded',
        //    and late payment on delivery_failed succeeds and sets NeedsRefund = true!
        var failOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L, confirmNow: true);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{failOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 700_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        using (var ovp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{failOrder.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan thu thua truoc giao",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, ovp.StatusCode);

        // Seed order shipment and ship -> delivery_failed.
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_at)
VALUES ('{Guid.CreateVersion7()}','{failOrder.Id}','order','manual','GHN','created',0,now());
""");
        uint failVer;
        await using (var db = app.OwnerContext())
            failVer = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == failOrder.Id)).Version;

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var ship = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>();
            var shipped = await ship.Handle(new MarkOrderShipping(failOrder.Id, failVer, app.OwnerId, "Xuat kho"), CancellationToken.None);
            Assert.True(shipped.IsSuccess, shipped.IsSuccess ? "" : shipped.Error.Code);
            Assert.Equal(OrderStatus.Shipping, shipped.Value.Status);
            Assert.Equal(OrderPaymentStatus.Paid, shipped.Value.PaymentStatus);

            var fail = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderDeliveryFailed, OrderSummary>>();
            var failed = await fail.Handle(new MarkOrderDeliveryFailed(failOrder.Id, shipped.Value.Version, OrderActorType.Staff, app.OwnerId, "Khach bom hang"), CancellationToken.None);
            Assert.True(failed.IsSuccess);
            Assert.Equal(OrderStatus.DeliveryFailed, failed.Value.Status);
            Assert.Equal(OrderPaymentStatus.PartiallyRefunded, failed.Value.PaymentStatus);
        }

        // Late payment on delivery_failed still accepted and sets NeedsRefund = true (BR-PAY-01 / O1=A).
        using var latePay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{failOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 50_000L,
            receivedAt = "2026-11-25T05:30:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, latePay.StatusCode);
        var lateDto = (await latePay.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        Assert.Equal(OrderStatus.DeliveryFailed, lateDto.OrderStatus);
        Assert.True(lateDto.NeedsRefund);
        Assert.False(lateDto.IsOverpaid);
    }

    [Fact]
    public async Task FrPay05AwaitingBankTransferUsesEffectivePaidFilterAfterOverpaymentRefundAndTotalIncrease()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 500_000L, suffix: "aw2");

        // Order total 500k, paid 700k, refunded 200k overpayment -> effectivePaid = 500k == total -> NOT awaiting!
        var order = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 1, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 700_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        using (var ovp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan tien thu thua",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, ovp.StatusCode);

        using (var beforeEdit = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer"))
        {
            Assert.Equal(HttpStatusCode.OK, beforeEdit.StatusCode);
            var page = (await beforeEdit.Content.ReadFromJsonAsync<AwaitingPaymentsPage>(WebJson))!;
            Assert.DoesNotContain(page.BankTransferItems, x => x.OrderId == order.Id);
        }

        // Increase order lines to qty 2 (total 1_000_000) -> effectivePaid 500k < 1_000k -> appears in awaiting with RemainingVnd = 500k!
        uint ver;
        await using (var db = app.OwnerContext())
            ver = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var res = await update.Handle(new UpdateOrderLines(order.Id, ver, true, [new(variantId, 2)], null, null, null, null, 0L), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            Assert.Equal(OrderPaymentStatus.PartiallyPaid, res.Value.PaymentStatus);
        }

        using var afterEdit = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer");
        Assert.Equal(HttpStatusCode.OK, afterEdit.StatusCode);
        var pageAfter = (await afterEdit.Content.ReadFromJsonAsync<AwaitingPaymentsPage>(WebJson))!;
        var item = Assert.Single(pageAfter.BankTransferItems, x => x.OrderId == order.Id);
        Assert.Equal(1_000_000L, item.TotalVnd);
        Assert.Equal(700_000L, item.PaidVnd);
        Assert.Equal(500_000L, item.EffectivePaidVnd);
        Assert.Equal(500_000L, item.RemainingVnd);
    }

    [Fact]
    public async Task FrPay05GetAwaitingPaymentsFiltersBankTransferAndCodByCarrierWithoutDuplicatesOrCostLeak()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 500_000L, suffix: "p12");

        // Bank transfer orders: 1 unpaid web (due soonest), 1 partially paid admin, 1 fully paid (excluded), 1 cancelled (excluded), 1 backfilled (excluded).
        var unpaidWeb = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer); // total 530k
        var partialAdmin = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L); // total 500k
        var paidAdmin = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);
        var cancelledAdmin = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);
        var backfilledAdmin = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, overrideShippingFeeVnd: 0L);

        using (var p = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{partialAdmin.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 200_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, p.StatusCode);

        using (var p = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{paidAdmin.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, p.StatusCode);

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            Assert.True((await cancel.Handle(new CancelOrder(cancelledAdmin.Id, cancelledAdmin.Version, OrderActorType.Staff, app.OwnerId, "Huy"), CancellationToken.None)).IsSuccess);
        }
        await app.ExecuteAsync($"UPDATE sales.orders SET is_backfilled = true WHERE id = '{backfilledAdmin.Id}'");

        // Invalid queries -> 400.
        using (var missingType = await app.Client.GetAsync("/api/v1/admin/payments/awaiting"))
            await AssertProblemAsync(missingType, HttpStatusCode.BadRequest, "validation_failed");
        using (var badType = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=cash"))
            await AssertProblemAsync(badType, HttpStatusCode.BadRequest, "validation_failed");
        using (var carrierOnBank = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer&carrier=GHN"))
            await AssertProblemAsync(carrierOnBank, HttpStatusCode.BadRequest, "validation_failed");

        // Valid bank_transfer query.
        using var bankResp = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, bankResp.StatusCode);
        var bankRaw = await bankResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cost", bankRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profit", bankRaw, StringComparison.OrdinalIgnoreCase);
        var bankPage = JsonSerializer.Deserialize<AwaitingPaymentsPage>(bankRaw, WebJson)!;
        Assert.Equal("bank_transfer", bankPage.Type);
        Assert.Equal(2L, bankPage.TotalItems);
        Assert.Equal(530_000L + 300_000L, bankPage.TotalAmountVnd);
        Assert.Empty(bankPage.CodItems);
        Assert.Equal(2, bankPage.BankTransferItems.Count);
        Assert.Equal(unpaidWeb.Id, bankPage.BankTransferItems[0].OrderId); // Due web order sorted before null-due admin order.
        Assert.Equal(530_000L, bankPage.BankTransferItems[0].RemainingVnd);
        Assert.Equal(partialAdmin.Id, bankPage.BankTransferItems[1].OrderId);
        Assert.Equal(300_000L, bankPage.BankTransferItems[1].RemainingVnd);

        // COD awaiting_settlement: seed 2 COD orders (GHN with an older failed shipment + a delivered shipment to prove 0 duplicates; GHTK with delivered shipment).
        var codGhnOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, overrideShippingFeeVnd: 0L);
        var codGhtkOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, overrideShippingFeeVnd: 0L);
        var ghnPaymentId = Guid.CreateVersion7();
        var ghtkPaymentId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,tracking_code,status,cod_amount_vnd,shipping_cost_vnd,created_at,failed_at)
VALUES ('{Guid.CreateVersion7()}','{codGhnOrder.Id}','order','manual','OldCarrier','OLD-001','failed',500000,25000,now() - interval '2 days',now() - interval '1 day');
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,tracking_code,status,cod_amount_vnd,shipping_cost_vnd,created_at,delivered_at)
VALUES ('{Guid.CreateVersion7()}','{codGhnOrder.Id}','order','manual','GHN','GHN-123','delivered',500000,31000,now() - interval '1 day',now() - interval '2 hours');
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,tracking_code,status,cod_amount_vnd,shipping_cost_vnd,created_at,delivered_at)
VALUES ('{Guid.CreateVersion7()}','{codGhtkOrder.Id}','order','manual','GHTK','GHTK-456','delivered',500000,28000,now() - interval '1 day',now() - interval '1 hour');
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,created_at)
VALUES ('{ghnPaymentId}','{codGhnOrder.Id}','cod','manual',500000,'awaiting_settlement',now() - interval '2 hours'),
       ('{ghtkPaymentId}','{codGhtkOrder.Id}','cod','manual',500000,'awaiting_settlement',now() - interval '1 hour');
""");

        using var codAllResp = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=cod");
        Assert.Equal(HttpStatusCode.OK, codAllResp.StatusCode);
        var codAllRaw = await codAllResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cost", codAllRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profit", codAllRaw, StringComparison.OrdinalIgnoreCase);
        var codAll = JsonSerializer.Deserialize<AwaitingPaymentsPage>(codAllRaw, WebJson)!;
        Assert.Equal(2L, codAll.TotalItems);
        Assert.Equal(1_000_000L, codAll.TotalAmountVnd);
        Assert.Empty(codAll.BankTransferItems);
        Assert.Equal(2, codAll.CodItems.Count);
        Assert.Equal("GHN", codAll.CodItems[0].CarrierName);
        Assert.Equal("GHN-123", codAll.CodItems[0].TrackingCode);

        // Filter by carrier case-insensitively.
        using var codGhnResp = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=cod&carrier=ghn");
        Assert.Equal(HttpStatusCode.OK, codGhnResp.StatusCode);
        var codGhn = (await codGhnResp.Content.ReadFromJsonAsync<AwaitingPaymentsPage>(WebJson))!;
        Assert.Equal(1L, codGhn.TotalItems);
        Assert.Equal(500_000L, codGhn.TotalAmountVnd);
        var singleItem = Assert.Single(codGhn.CodItems);
        Assert.Equal(ghnPaymentId, singleItem.PaymentId);
        Assert.Equal("GHN", singleItem.CarrierName);
    }

    [Fact]
    public async Task LegacyZeroVndUnpaidWebOrderSelfHealsOnConfirmAndShippingAndIsCountedCorrectlyByDailyDigest()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "zlg");

        // Create a 0-VND web bank_transfer order (100% discount via admin/SQL simulation of pre-P2-07 base row):
        // origin='web', status='pending', payment_method='bank_transfer', total_vnd=0, payment_status='unpaid', payment_due_at=now()+12h.
        var order = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        await app.ExecuteAsync($"""
UPDATE sales.orders
SET subtotal_vnd = 500000,
    discount_vnd = 500000,
    shipping_fee_vnd = 0,
    total_vnd = 0,
    payment_status = 'unpaid',
    payment_due_at = now() + interval '12 hours'
WHERE id = '{order.Id}';
""");

        // 1. Daily digest must count the legacy 0-VND unpaid web bank_transfer order in PendingConfirmationOrders
        //    and MUST NOT count it in ExpiringPaymentOrders.
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var digestRead = scope.ServiceProvider.GetRequiredService<IDailyDigestRead>();
            var snapshot = await digestRead.ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(1L, snapshot.PendingConfirmationOrders);
            Assert.Equal(0L, snapshot.ExpiringPaymentOrders);
        }

        // 2. ConfirmOrder self-heals payment_status -> 'paid' under L1 lock and succeeds cleanly!
        uint verAfterSeed;
        await using (var db = app.OwnerContext())
            verAfterSeed = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;

        OrderSummary confirmed;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var confirm = scope.ServiceProvider.GetRequiredService<ICommandHandler<ConfirmOrder, OrderSummary>>();
            var res = await confirm.Handle(new ConfirmOrder(order.Id, verAfterSeed, OrderActorType.Staff, app.OwnerId, false, "Xac nhan don 0d"), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            confirmed = res.Value;
        }
        Assert.Equal(OrderStatus.Confirmed, confirmed.Status);
        Assert.Equal(OrderPaymentStatus.Paid, confirmed.PaymentStatus);
        Assert.Equal("paid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{order.Id}'"));

        // 3. Also verify MarkOrderShipping self-heals a legacy confirmed + unpaid 0-VND bank_transfer row.
        await app.ExecuteAsync($"""
UPDATE sales.orders SET payment_status = 'unpaid' WHERE id = '{order.Id}';
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_at)
VALUES ('{Guid.CreateVersion7()}','{order.Id}','order','manual','GHN','created',0,now());
""");
        uint verBeforeShip;
        await using (var db = app.OwnerContext())
            verBeforeShip = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var ship = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>();
            var shipped = await ship.Handle(new MarkOrderShipping(order.Id, verBeforeShip, app.OwnerId, "Xuat kho don 0d"), CancellationToken.None);
            Assert.True(shipped.IsSuccess, shipped.IsSuccess ? "" : shipped.Error.Code);
            Assert.Equal(OrderStatus.Shipping, shipped.Value.Status);
            Assert.Equal(OrderPaymentStatus.Paid, shipped.Value.PaymentStatus);
        }
        Assert.Equal("paid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{order.Id}'"));
    }

    [Fact]
    public async Task BrPay03AutoConfirmUsesLedgerCalculatedPriorPaidStatusAcrossZeroVndAndDriftedOrders()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 500_000L, suffix: "p13");

        // 1. Legacy 0-VND web bank_transfer pending order with stale DB payment_status = 'unpaid':
        //    recording a positive payment self-heals payment_status = 'paid', flags isOverpaid & needsRefund,
        //    does NOT auto-confirm, emits 0 OrderConfirmed outbox events, and replays identically on same Idempotency-Key.
        var legacyZeroOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        await app.ExecuteAsync($"""
UPDATE sales.orders
SET subtotal_vnd = 500000,
    discount_vnd = 500000,
    shipping_fee_vnd = 0,
    total_vnd = 0,
    payment_status = 'unpaid'
WHERE id = '{legacyZeroOrder.Id}';
""");

        var legacyKey = Guid.CreateVersion7().ToString("D");
        var legacyPayload = new
        {
            method = "bank_transfer",
            amountVnd = 50_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-LEGACY-ZERO-1"
        };
        string legacyFirstRaw;
        using (var first = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{legacyZeroOrder.Id}/payments", legacyPayload, legacyKey))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            legacyFirstRaw = await first.Content.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<PaymentRecordedView>(legacyFirstRaw, WebJson)!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.Equal(50_000L, dto.PaidVnd);
            Assert.Equal(50_000L, dto.NetPaidVnd);
            Assert.True(dto.IsOverpaid);
            Assert.True(dto.NeedsRefund);
            Assert.False(dto.AutoConfirmed);
        }

        Assert.Equal("pending", await ScalarAsync<string>(app, $"SELECT status FROM sales.orders WHERE id='{legacyZeroOrder.Id}'"));
        Assert.Equal("paid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{legacyZeroOrder.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_status_history WHERE order_id='{legacyZeroOrder.Id}' AND to_status='confirmed'"));

        using (var replay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{legacyZeroOrder.Id}/payments", legacyPayload, legacyKey))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(legacyFirstRaw, await replay.Content.ReadAsStringAsync());
        }
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.payments WHERE order_id='{legacyZeroOrder.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));

        // 2. New 0-VND web bank_transfer pending order (created directly as payment_status = 'paid'):
        //    recording a positive payment stays pending, autoConfirmed == false, 0 OrderConfirmed outbox events.
        await app.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,status)
VALUES ('{Guid.CreateVersion7()}','FREE500K','manual','fixed',500000,0,now() - interval '1 day',now() + interval '1 day','active');
UPDATE platform.settings SET value = '0' WHERE key = 'shipping.flat_fee_vnd';
""");
        var newZeroOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer, voucherCode: "FREE500K");
        await app.ExecuteAsync("UPDATE platform.settings SET value = '30000' WHERE key = 'shipping.flat_fee_vnd';");
        Assert.Equal(0L, newZeroOrder.TotalVnd);
        Assert.Equal(OrderStatus.Pending, newZeroOrder.Status);
        Assert.Equal(OrderPaymentStatus.Paid, newZeroOrder.PaymentStatus);

        using (var newZeroResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{newZeroOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 50_000L,
            receivedAt = "2026-11-25T05:05:00Z",
            bankReference = "FT-NEW-ZERO-1"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, newZeroResp.StatusCode);
            var dto = (await newZeroResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.True(dto.IsOverpaid);
            Assert.True(dto.NeedsRefund);
            Assert.False(dto.AutoConfirmed);
        }
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_status_history WHERE order_id='{newZeroOrder.Id}' AND to_status='confirmed'"));

        // 3. Positive-total web bank_transfer pending order with stale DB payment_status = 'unpaid'
        //    while its ledger already has confirmedPaid >= total:
        //    recording another positive payment self-heals payment_status = 'paid', marks isOverpaid = true,
        //    does NOT auto-confirm, emits 0 OrderConfirmed outbox events.
        var driftedPaidOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        Assert.Equal(530_000L, driftedPaidOrder.TotalVnd);
        await app.ExecuteAsync($"""
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,bank_reference,received_at,confirmed_at,confirmed_by,created_by,created_at)
VALUES ('{Guid.CreateVersion7()}','{driftedPaidOrder.Id}','bank_transfer','manual',530000,'confirmed','FT-DRIFT-PRIOR',now() - interval '10 minutes',now() - interval '10 minutes','{app.OwnerId}','{app.OwnerId}',now() - interval '10 minutes');
""");
        Assert.Equal("unpaid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{driftedPaidOrder.Id}'"));

        using (var driftedPaidResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{driftedPaidOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 100_000L,
            receivedAt = "2026-11-25T05:10:00Z",
            bankReference = "FT-DRIFT-EXTRA"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, driftedPaidResp.StatusCode);
            var dto = (await driftedPaidResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.Equal(630_000L, dto.PaidVnd);
            Assert.Equal(630_000L, dto.NetPaidVnd);
            Assert.True(dto.IsOverpaid);
            Assert.True(dto.NeedsRefund);
            Assert.False(dto.AutoConfirmed);
        }
        Assert.Equal("pending", await ScalarAsync<string>(app, $"SELECT status FROM sales.orders WHERE id='{driftedPaidOrder.Id}'"));
        Assert.Equal("paid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id='{driftedPaidOrder.Id}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));
        Assert.Equal(0L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_status_history WHERE order_id='{driftedPaidOrder.Id}' AND to_status='confirmed'"));

        // 4a. Positive-total web bank_transfer pending order with stale DB payment_status = 'paid' while ledger is unpaid:
        //     recording payment reaching paid transitions cleanly per OrderPaymentStatusCalculator and emits 1 OrderConfirmed.
        var stalePaidOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        await app.ExecuteAsync($"UPDATE sales.orders SET payment_status = 'paid' WHERE id = '{stalePaidOrder.Id}';");

        using (var stalePaidResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{stalePaidOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = stalePaidOrder.TotalVnd,
            receivedAt = "2026-11-25T05:15:00Z",
            bankReference = "FT-STALE-PAID-RECOVER"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, stalePaidResp.StatusCode);
            var dto = (await stalePaidResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Confirmed, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.False(dto.IsOverpaid);
            Assert.False(dto.NeedsRefund);
            Assert.True(dto.AutoConfirmed);
        }
        Assert.Equal("confirmed", await ScalarAsync<string>(app, $"SELECT status FROM sales.orders WHERE id='{stalePaidOrder.Id}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.order_status_history WHERE order_id='{stalePaidOrder.Id}' AND to_status='confirmed' AND actor_type='system' AND reason='{OrderConfirmReasonCodes.AutoConfirmedOnFullPayment}'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));

        // 4b. Normal ledger-unpaid -> ledger-partial -> ledger-paid transition still auto-confirms cleanly and emits 1 OrderConfirmed.
        var normalOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        using (var partResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{normalOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 230_000L,
            receivedAt = "2026-11-25T05:20:00Z",
            bankReference = "FT-NORMAL-PART"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, partResp.StatusCode);
            var dto = (await partResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Pending, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.PartiallyPaid, dto.OrderPaymentStatus);
            Assert.False(dto.AutoConfirmed);
        }
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM platform.outbox_messages WHERE type='{OrderEventTypes.OrderConfirmed}'"));

        using (var finalResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{normalOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = "2026-11-25T05:25:00Z",
            bankReference = "FT-NORMAL-FULL"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, finalResp.StatusCode);
            var dto = (await finalResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderStatus.Confirmed, dto.OrderStatus);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
            Assert.True(dto.AutoConfirmed);
        }

        await using (var db = app.OwnerContext())
        {
            var confirmedIds = (await db.OutboxMessages.AsNoTracking()
                .Where(x => x.Type == OrderEventTypes.OrderConfirmed)
                .ToListAsync())
                .Select(m =>
                {
                    Assert.True(OrderOutboxMessage.TryParse(m.Payload, out var parsed));
                    return parsed.OrderId;
                })
                .ToHashSet();

            Assert.Equal(2, confirmedIds.Count);
            Assert.Contains(stalePaidOrder.Id, confirmedIds);
            Assert.Contains(normalOrder.Id, confirmedIds);
            Assert.DoesNotContain(legacyZeroOrder.Id, confirmedIds);
            Assert.DoesNotContain(newZeroOrder.Id, confirmedIds);
            Assert.DoesNotContain(driftedPaidOrder.Id, confirmedIds);
        }
    }
}
