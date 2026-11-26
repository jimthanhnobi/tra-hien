using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Payments;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Payments;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class PaymentVoidAndRefundTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Rp20101BrPay06VoidConfirmedAndAwaitingPaymentSetsConfirmedAtNullPreservesAuditAndRecalculatesOrder()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "v1");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        // 1. Record a confirmed bank transfer payment (500k -> order paid).
        using var recResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-VOID-001",
            note = "Ghi nhan nham 0901234567"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, recResp.StatusCode);
        var recorded = (await recResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        Assert.Equal(OrderPaymentStatus.Paid, recorded.OrderPaymentStatus);

        // 2. Owner voids the confirmed payment -> R-P201-01 sets confirmed_at=NULL without 23514 ck_payments_confirmed!
        const string sensitiveVoidReason = "Nhap nham tu giao dich cua anh Minh 0909888777";
        using var voidResp = await app.MutateAsync(
            HttpMethod.Post,
            $"/api/v1/admin/payments/{recorded.PaymentId}/void",
            new { reason = sensitiveVoidReason });
        Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        var voided = (await voidResp.Content.ReadFromJsonAsync<PaymentVoidedView>(WebJson))!;
        Assert.Equal(PaymentRecordStatus.Voided, voided.Status);
        Assert.Equal(OrderPaymentStatus.Unpaid, voided.OrderPaymentStatus);
        Assert.Equal(0L, voided.PaidVnd);
        Assert.Equal(0L, voided.NetPaidVnd);

        await using (var db = app.OwnerContext())
        {
            var paymentRow = await db.Set<Payment>().AsNoTracking().SingleAsync(x => x.Id == recorded.PaymentId);
            Assert.Equal(PaymentRecordStatus.Voided, paymentRow.Status);
            Assert.Null(paymentRow.ConfirmedAt);
            Assert.Equal(sensitiveVoidReason, paymentRow.VoidReason);

            var voidAudit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(x => x.EntityType == "sales.payments" && x.EntityId == recorded.PaymentId && x.Action == "update");
            Assert.DoesNotContain("0909888777", voidAudit.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain("anh Minh", voidAudit.Changes, StringComparison.Ordinal);

            using var changes = JsonDocument.Parse(voidAudit.Changes);
            var root = changes.RootElement;
            Assert.Equal("confirmed", root.GetProperty("status").GetProperty("before").GetString());
            Assert.Equal("voided", root.GetProperty("status").GetProperty("after").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("confirmed_at").GetProperty("before").ValueKind);
            Assert.NotEqual("[redacted]", root.GetProperty("confirmed_at").GetProperty("before").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("confirmed_at").GetProperty("after").ValueKind);
            Assert.Equal("[redacted]", root.GetProperty("void_reason").GetProperty("after").GetString());
        }

        // 3. Voiding an unsettled COD awaiting_settlement payment (cod_settlement_id IS NULL) also succeeds cleanly.
        var codOrder = await CreateAdminOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.Cod, overrideShippingFeeVnd: 0L);
        var awaitingPaymentId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,created_at)
VALUES ('{awaitingPaymentId}','{codOrder.Id}','cod','manual',500000,'awaiting_settlement',now());
""");
        using var voidCodResp = await app.MutateAsync(
            HttpMethod.Post,
            $"/api/v1/admin/payments/{awaitingPaymentId}/void",
            new { reason = "Huy COD chua doi soat do nhap sai" });
        Assert.Equal(HttpStatusCode.OK, voidCodResp.StatusCode);
        var voidedCod = (await voidCodResp.Content.ReadFromJsonAsync<PaymentVoidedView>(WebJson))!;
        Assert.Equal(PaymentRecordStatus.Voided, voidedCod.Status);
        Assert.Equal(OrderPaymentStatus.Unpaid, voidedCod.OrderPaymentStatus);
    }

    [Fact]
    public async Task BrPay06VoidPaymentBlocksWhenCodSettlementLinkedAlreadyVoidedOrAlreadyRefunded()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "v2");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        // (1) Settled COD payment with cod_settlement_id IS NOT NULL -> 409 payment_settled_cod.
        var settlementId = Guid.CreateVersion7();
        var settledPaymentId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
INSERT INTO sales.cod_settlements(id,carrier_name,reference,settled_at,gross_cod_vnd,fee_vnd,net_received_vnd,created_by,created_at)
VALUES ('{settlementId}','GHN','COD-REF-1',now(),500000,15000,485000,'{app.OwnerId}',now());
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,received_at,cod_settlement_id,created_by,created_at,confirmed_by,confirmed_at)
VALUES ('{settledPaymentId}','{order.Id}','cod','manual',500000,'confirmed',now(),'{settlementId}','{app.OwnerId}',now(),'{app.OwnerId}',now());
""");
        using (var settledResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{settledPaymentId}/void", new { reason = "Thu void COD da doi soat" }))
            await AssertProblemAsync(settledResp, HttpStatusCode.Conflict, "payment_settled_cod");

        // (2) Blank reason -> 400 validation_failed.
        using (var blankReason = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{settledPaymentId}/void", new { reason = "   " }))
            await AssertProblemAsync(blankReason, HttpStatusCode.BadRequest, "validation_failed");

        // (3) Already voided -> 409 payment_already_voided.
        var order2 = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        using var rec2 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order2.Id}/payments", new
        {
            method = "cash",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        var p2 = (await rec2.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        using (var firstVoid = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{p2.PaymentId}/void", new { reason = "Huy lan 1" }))
            Assert.Equal(HttpStatusCode.OK, firstVoid.StatusCode);
        using (var secondVoid = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{p2.PaymentId}/void", new { reason = "Huy lan 2" }))
            await AssertProblemAsync(secondVoid, HttpStatusCode.Conflict, "payment_already_voided");

        // (4) Already refunded (ASSUMPTION-P207-01=A) -> 409 payment_has_refunds.
        var order3 = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        using var rec3 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order3.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 600_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        var p3 = (await rec3.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        using (var refund3 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order3.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Hoan thu thua 100k",
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, refund3.StatusCode);
        }
        using (var blockedVoid = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{p3.PaymentId}/void", new { reason = "Thu void khi da hoan" }))
            await AssertProblemAsync(blockedVoid, HttpStatusCode.Conflict, "payment_has_refunds");

        // (5) Backfilled historical order -> 422 backfilled_order_immutable without mutating payment or order.
        var backfilledOrder = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        var backfilledPaymentId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET is_backfilled = true, status = 'delivered', delivered_at = now(), payment_status = 'unpaid' WHERE id = '{backfilledOrder.Id}';
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,received_at,created_by,created_at,confirmed_by,confirmed_at)
VALUES ('{backfilledPaymentId}','{backfilledOrder.Id}','bank_transfer','manual',500000,'confirmed',now(),'{app.OwnerId}',now(),'{app.OwnerId}',now());
""");
        using (var backfilledVoid = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{backfilledPaymentId}/void", new { reason = "Thu void don backfill" }))
            await AssertProblemAsync(backfilledVoid, HttpStatusCode.UnprocessableEntity, "backfilled_order_immutable");
        Assert.Equal("confirmed", await ScalarAsync<string>(app, $"SELECT status FROM sales.payments WHERE id = '{backfilledPaymentId}'"));
        Assert.Equal("unpaid", await ScalarAsync<string>(app, $"SELECT payment_status FROM sales.orders WHERE id = '{backfilledOrder.Id}'"));
    }

    [Fact]
    public async Task BrPay07CancellationRefundEnforcesNetCeilingIdempotencyStatusMatrixAndUpdatesPaymentStatus()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "r1");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 30_000L);

        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        var refundPath = $"/api/v1/admin/orders/{order.Id}/refunds";

        // Cancellation kind forbidden on pending order -> 422 invalid_refund_kind_for_order_status.
        using (var wrongStatus = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "cancellation",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Chua huy don",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(wrongStatus, HttpStatusCode.UnprocessableEntity, "invalid_refund_kind_for_order_status");

        // Cancel order -> now cancellation refund is allowed, while overpayment/adjustment/return are forbidden.
        uint ver;
        await using (var db = app.OwnerContext())
            ver = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            Assert.True((await cancel.Handle(new CancelOrder(order.Id, ver, OrderActorType.Staff, app.OwnerId, "Khach huy"), CancellationToken.None)).IsSuccess);
        }

        // Overpayment forbidden on cancelled order.
        using (var ovpCancelled = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Sai loai tren don huy",
            refundedAt = "2026-11-25T05:08:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(ovpCancelled, HttpStatusCode.UnprocessableEntity, "invalid_refund_kind_for_order_status");

        // Cancellation with shippingAmountVnd > 0 rejected -> 422 shipping_refund_not_allowed.
        using (var shipNotAllowed = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "cancellation",
            amountVnd = 100_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Khong cho tach ship tren cancellation",
            refundedAt = "2026-11-25T05:08:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(shipNotAllowed, HttpStatusCode.UnprocessableEntity, "shipping_refund_not_allowed");

        // Over net ceiling -> 422 refund_exceeds_net_paid.
        using (var over = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "cancellation",
            amountVnd = 600_000L,
            method = "bank_transfer",
            reason = "Hoan vuot net",
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(over, HttpStatusCode.UnprocessableEntity, "refund_exceeds_net_paid");

        // Partial refund 200k + idempotency replay & mismatch.
        var key1 = Guid.CreateVersion7().ToString("D");
        var refund1Payload = new
        {
            kind = "cancellation",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan mot phan don huy",
            refundedAt = "2026-11-25T05:15:00Z"
        };
        using var r1 = await PostIdempotentJsonAsync(app.Client, refundPath, refund1Payload, key1);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var r1Raw = await r1.Content.ReadAsStringAsync();
        var r1Dto = JsonSerializer.Deserialize<RefundRecordedView>(r1Raw, WebJson)!;
        Assert.Equal(RefundKind.Cancellation, r1Dto.Kind);
        Assert.Equal(0L, r1Dto.ShippingAmountVnd);
        Assert.Equal(OrderPaymentStatus.PartiallyRefunded, r1Dto.OrderPaymentStatus);
        Assert.Equal(500_000L, r1Dto.PaidVnd);
        Assert.Equal(200_000L, r1Dto.RefundedVnd);
        Assert.Equal(300_000L, r1Dto.NetPaidVnd);
        Assert.True(r1Dto.NeedsRefund);
        Assert.Null(r1Dto.ReturnRequestId);
        Assert.Null(r1Dto.ReturnRequestStatus);

        using var r1Replay = await PostIdempotentJsonAsync(app.Client, refundPath, refund1Payload, key1);
        Assert.Equal(HttpStatusCode.OK, r1Replay.StatusCode);
        Assert.Equal(r1Raw, await r1Replay.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.refunds WHERE order_id='{order.Id}'"));

        using (var r1Mismatch = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "cancellation",
            amountVnd = 250_000L,
            method = "bank_transfer",
            reason = "Hoan mot phan don huy",
            refundedAt = "2026-11-25T05:15:00Z"
        }, key1))
            await AssertProblemAsync(r1Mismatch, HttpStatusCode.Conflict, "idempotency_key_reused");

        // Remaining 300k -> refunded, net = 0, needsRefund = false.
        using var r2 = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "cancellation",
            amountVnd = 300_000L,
            method = "cash",
            reason = "Hoan not phan con lai",
            refundedAt = "2026-11-25T05:20:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var r2Dto = (await r2.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
        Assert.Equal(OrderPaymentStatus.Refunded, r2Dto.OrderPaymentStatus);
        Assert.Equal(500_000L, r2Dto.RefundedVnd);
        Assert.Equal(0L, r2Dto.NetPaidVnd);
        Assert.False(r2Dto.NeedsRefund);
    }

    [Fact]
    public async Task BrPay07ConcurrentRefundsWithDistinctKeysSerializeOnOrderLockAndNeverExceedNet()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "r2");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        // Cancel order to race 10 concurrent cancellation refunds of 300k each against net=500k.
        uint ver;
        await using (var db = app.OwnerContext())
            ver = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOrder, OrderSummary>>();
            Assert.True((await cancel.Handle(new CancelOrder(order.Id, ver, OrderActorType.Staff, app.OwnerId, "Huy de test race"), CancellationToken.None)).IsSuccess);
        }

        const int concurrency = 10;
        var csrf = await GetCsrfTokenAsync(app.Client);
        var path = $"/api/v1/admin/orders/{order.Id}/refunds";
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref ready) == concurrency)
                startGate.TrySetResult();
            await startGate.Task;

            using var resp = await PostIdempotentJsonAsync(app.Client, path, new
            {
                kind = "cancellation",
                amountVnd = 300_000L,
                method = "bank_transfer",
                reason = $"Concurrent refund #{i}",
                refundedAt = "2026-11-25T05:10:00Z"
            }, Guid.CreateVersion7().ToString("D"), csrf);
            return (resp.StatusCode, body: await resp.Content.ReadAsStringAsync());
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        var successes = results.Where(r => r.StatusCode == HttpStatusCode.OK).ToArray();
        var rejected = results.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToArray();

        Assert.Single(successes);
        Assert.Equal(concurrency - 1, rejected.Length);
        Assert.All(rejected, r =>
        {
            using var doc = JsonDocument.Parse(r.body);
            Assert.Equal("refund_exceeds_net_paid", doc.RootElement.GetProperty("code").GetString());
        });

        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM sales.refunds WHERE order_id='{order.Id}'"));
        Assert.Equal(300_000L, await ScalarAsync<long>(app, $"SELECT COALESCE(SUM(amount_vnd), 0)::bigint FROM sales.refunds WHERE order_id='{order.Id}'"));
    }

    [Fact]
    public async Task O3aCOverpaymentRefundKeepsPaidAllowsMarkShippingAndWorksAfterReturnRefundOnDeliveredOrder()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 470_000L, suffix: "o3a");

        // 1. Web bank_transfer order (470k + 30k = 500k), customer transfers 700k (200k overpaid, auto-confirmed).
        var webOrder = await CreateWebBankOrderAsync(app, customerId, variantId, paymentMethod: OrderPaymentMethod.BankTransfer);
        Assert.Equal(500_000L, webOrder.TotalVnd);

        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 700_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
            var payDto = (await pay.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.True(payDto.AutoConfirmed);
            Assert.True(payDto.IsOverpaid);
            Assert.True(payDto.NeedsRefund);
        }

        // Overpayment > 200k -> 422 refund_exceeds_overpayment.
        using (var over = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webOrder.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_001L,
            method = "bank_transfer",
            reason = "Vuot tien thu thua",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(over, HttpStatusCode.UnprocessableEntity, "refund_exceeds_overpayment");

        // Refund exact 200k overpayment -> payment_status STAYS 'paid', IsOverpaid=false, NeedsRefund=false!
        using (var ovp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webOrder.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan dung tien thu thua",
            refundedAt = "2026-11-25T05:06:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ovp.StatusCode);
            var ovpDto = (await ovp.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.Paid, ovpDto.OrderPaymentStatus);
            Assert.False(ovpDto.IsOverpaid);
            Assert.False(ovpDto.NeedsRefund);
            Assert.Equal(500_000L, ovpDto.NetPaidVnd);
        }

        // Seed shipment and ship -> Order.MarkShipping SUCCEEDS (does not block bank_transfer because PaymentStatus == Paid)!
        await app.ExecuteAsync($"""
INSERT INTO sales.shipments(id,order_id,purpose,provider,carrier_name,status,cod_amount_vnd,created_at)
VALUES ('{Guid.CreateVersion7()}','{webOrder.Id}','order','manual','GHN','created',0,now());
""");
        uint ver;
        await using (var db = app.OwnerContext())
            ver = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == webOrder.Id)).Version;

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var ship = scope.ServiceProvider.GetRequiredService<ICommandHandler<MarkOrderShipping, OrderSummary>>();
            var shipped = await ship.Handle(new MarkOrderShipping(webOrder.Id, ver, app.OwnerId, "Xuat kho sau hoan thu thua"), CancellationToken.None);
            Assert.True(shipped.IsSuccess, shipped.IsSuccess ? "" : shipped.Error.Code);
            Assert.Equal(OrderStatus.Shipping, shipped.Value.Status);

            // Adjustment is FORBIDDEN on 'shipping' orders!
            using var adjOnShipping = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{webOrder.Id}/refunds", new
            {
                kind = "adjustment",
                amountVnd = 50_000L,
                method = "bank_transfer",
                reason = "Cam adjustment tren shipping",
                refundedAt = "2026-11-25T05:10:00Z"
            }, Guid.CreateVersion7().ToString("D"));
            await AssertProblemAsync(adjOnShipping, HttpStatusCode.UnprocessableEntity, "invalid_refund_kind_for_order_status");
        }

        // 2. Delivered overpaid order (500k total, 700k paid): Return refund FIRST (500k), THEN Overpayment refund (200k)!
        var deliveredOverpaid = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 30_000L);
        Assert.Equal(500_000L, deliveredOverpaid.TotalVnd);
        using (var pay2 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{deliveredOverpaid.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 700_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay2.StatusCode);

        var returnId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{deliveredOverpaid.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES ('{returnId}','DT-261126-101','{deliveredOverpaid.Id}','damaged_in_transit','Vo toan bo','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{returnId}','{deliveredOverpaid.Lines[0].Id}',1,false);
""");

        // Step A: Return refund 470k goods + 30k shipping = 500k -> net=200k, IsOverpaid=true, NeedsRefund=true!
        using (var retResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{deliveredOverpaid.Id}/refunds", new
        {
            kind = "return",
            amountVnd = 500_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Hoan hang + ship",
            returnRequestId = returnId,
            refundedAt = "2026-11-25T05:15:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, retResp.StatusCode);
            var retDto = (await retResp.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.PartiallyRefunded, retDto.OrderPaymentStatus);
            Assert.Equal(200_000L, retDto.NetPaidVnd);
            Assert.True(retDto.IsOverpaid);
            Assert.True(retDto.NeedsRefund);
        }

        // Step B: Overpayment refund 200k -> refunded, net=0, IsOverpaid=false, NeedsRefund=false!
        using var ovpAfterRet = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{deliveredOverpaid.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 200_000L,
            method = "bank_transfer",
            reason = "Hoan not 200k thu thua sau return",
            refundedAt = "2026-11-25T05:20:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, ovpAfterRet.StatusCode);
        var finalDto = (await ovpAfterRet.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
        Assert.Equal(OrderPaymentStatus.Refunded, finalDto.OrderPaymentStatus);
        Assert.Equal(0L, finalDto.NetPaidVnd);
        Assert.False(finalDto.IsOverpaid);
        Assert.False(finalDto.NeedsRefund);
    }

    [Fact]
    public async Task O3bMultiplePartialReturnsCumulativeHalfUp800kAnd700kWithReplacementShippingAndNeedsRefund()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 20, priceVnd: 300_000L, suffix: "cum");

        // === Scenario 1: 800k netMerch (3 x 300k - 100k discount) + 30k shipping = 830k ===
        var order800 = await CreateAdminOrderAsync(
            app, customerId, variantId, quantity: 3,
            manualDiscountVnd: 100_000L, discountNote: "Giam 100k", overrideShippingFeeVnd: 30_000L);
        Assert.Equal(830_000L, order800.TotalVnd);

        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order800.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 830_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        var r1Id = Guid.CreateVersion7();
        var r2Id = Guid.CreateVersion7();
        var r3Id = Guid.CreateVersion7();
        var emptyLinesId = Guid.CreateVersion7();
        var line800Id = order800.Lines[0].Id;

        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{order800.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES
  ('{emptyLinesId}','DT-261126-200','{order800.Id}','defective','Phieu rong','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{r1Id}','DT-261126-201','{order800.Id}','damaged_in_transit','Tra mon 1','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{r2Id}','DT-261126-202','{order800.Id}','damaged_in_transit','Tra mon 2','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{r3Id}','DT-261126-203','{order800.Id}','damaged_in_transit','Tra mon 3','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES
  ('{Guid.CreateVersion7()}','{r1Id}','{line800Id}',1,false),
  ('{Guid.CreateVersion7()}','{r2Id}','{line800Id}',1,false),
  ('{Guid.CreateVersion7()}','{r3Id}','{line800Id}',1,false);
""");

        var path800 = $"/api/v1/admin/orders/{order800.Id}/refunds";

        // Fail-closed: return request with 0 lines -> 422 return_request_has_no_lines.
        using (var emptyResp = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 10_000L,
            method = "bank_transfer",
            reason = "Phieu rong",
            returnRequestId = emptyLinesId,
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(emptyResp, HttpStatusCode.UnprocessableEntity, "return_request_has_no_lines");
        // Reject the empty return request so it does not affect NeedsRefund test below.
        await app.ExecuteAsync($"UPDATE sales.return_requests SET status='rejected' WHERE id='{emptyLinesId}'");

        // Return #1: shipping rejected because partial return (1/3) -> 422 shipping_refund_requires_full_return.
        using (var shipPartial = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 266_667L + 30_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Thu hoan ship khi tra 1/3",
            returnRequestId = r1Id,
            refundedAt = "2026-11-25T05:06:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(shipPartial, HttpStatusCode.UnprocessableEntity, "shipping_refund_requires_full_return");

        // Return #1 at exact cap 266_667 -> 200; NeedsRefund stays TRUE because r2 & r3 are still approved/refund!
        using (var ok1 = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 266_667L,
            method = "bank_transfer",
            reason = "Hoan mon 1",
            returnRequestId = r1Id,
            refundedAt = "2026-11-25T05:07:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ok1.StatusCode);
            var dto1 = (await ok1.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(ReturnRequestStatus.Resolved, dto1.ReturnRequestStatus);
            Assert.True(dto1.NeedsRefund);
        }

        // Return #2: 266_667 exceeds cumulative cap 266_666 -> 422 refund_exceeds_return_lines!
        using (var over2 = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 266_667L,
            method = "bank_transfer",
            reason = "Vuot 1d o lan 2",
            returnRequestId = r2Id,
            refundedAt = "2026-11-25T05:08:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(over2, HttpStatusCode.UnprocessableEntity, "refund_exceeds_return_lines");

        // Return #2 at exact cap 266_666 -> 200; NeedsRefund still TRUE (r3 still approved/refund).
        using (var ok2 = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 266_666L,
            method = "bank_transfer",
            reason = "Hoan mon 2",
            returnRequestId = r2Id,
            refundedAt = "2026-11-25T05:09:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ok2.StatusCode);
            var dto2 = (await ok2.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.True(dto2.NeedsRefund);
        }

        // Return #3 completes 3/3 -> exact goods cap 266_667 + 30k shipping = 296_667 -> 200; NeedsRefund drops to FALSE!
        using (var ok3 = await PostIdempotentJsonAsync(app.Client, path800, new
        {
            kind = "return",
            amountVnd = 296_667L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Hoan mon 3 + phi ship",
            returnRequestId = r3Id,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, ok3.StatusCode);
            var dto3 = (await ok3.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(30_000L, dto3.ShippingAmountVnd);
            Assert.Equal(OrderPaymentStatus.Refunded, dto3.OrderPaymentStatus);
            Assert.Equal(0L, dto3.NetPaidVnd);
            Assert.False(dto3.NeedsRefund);
        }

        // === Scenario 2: 700k netMerch (3 x 300k - 200k discount) + 30k shipping = 730k, with REPLACEMENT interleaved ===
        var order700 = await CreateAdminOrderAsync(
            app, customerId, variantId, quantity: 3,
            manualDiscountVnd: 200_000L, discountNote: "Giam 200k", overrideShippingFeeVnd: 30_000L);
        Assert.Equal(730_000L, order700.TotalVnd);

        using (var pay700 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order700.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 730_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay700.StatusCode);

        var repId = Guid.CreateVersion7();
        var s1Id = Guid.CreateVersion7();
        var s2Id = Guid.CreateVersion7();
        var line700Id = order700.Lines[0].Id;

        // Seed 1 replacement (qty 1, resolved) + 2 refund returns (qty 1 each -> total refund qty 2/3, NOT 3/3!).
        // Also set order_lines.returned_quantity = 3 to prove we NEVER trust returned_quantity for full-return shipping!
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{order700.Id}';
UPDATE sales.order_lines SET returned_quantity=3 WHERE id='{line700Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,resolved_at,created_by)
VALUES
  ('{repId}','DT-261126-300','{order700.Id}','damaged_in_transit','Doi 1 mon','resolved','replacement',now(),'{app.OwnerId}',now(),now(),'{app.OwnerId}'),
  ('{s1Id}','DT-261126-301','{order700.Id}','defective','Hoan 1 mon','approved','refund',now(),'{app.OwnerId}',now(),NULL,'{app.OwnerId}'),
  ('{s2Id}','DT-261126-302','{order700.Id}','defective','Hoan mon thu 2','approved','refund',now(),'{app.OwnerId}',now(),NULL,'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES
  ('{Guid.CreateVersion7()}','{repId}','{line700Id}',1,false),
  ('{Guid.CreateVersion7()}','{s1Id}','{line700Id}',1,false),
  ('{Guid.CreateVersion7()}','{s2Id}','{line700Id}',1,false);
""");

        var path700 = $"/api/v1/admin/orders/{order700.Id}/refunds";

        // s1: 233_334 rejected (half-up for 700k/3 is 233_333, NOT 233_334!).
        using (var over700 = await PostIdempotentJsonAsync(app.Client, path700, new
        {
            kind = "return",
            amountVnd = 233_334L,
            method = "bank_transfer",
            reason = "Thu 233334",
            returnRequestId = s1Id,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(over700, HttpStatusCode.UnprocessableEntity, "refund_exceeds_return_lines");

        using (var ok700_1 = await PostIdempotentJsonAsync(app.Client, path700, new
        {
            kind = "return",
            amountVnd = 233_333L,
            method = "bank_transfer",
            reason = "Hoan 233333",
            returnRequestId = s1Id,
            refundedAt = "2026-11-25T05:11:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, ok700_1.StatusCode);

        // s2: even though returned_quantity == 3 (1 replacement + 2 refund), shipping refund MUST be blocked!
        using (var shipWithReplacement = await PostIdempotentJsonAsync(app.Client, path700, new
        {
            kind = "return",
            amountVnd = 233_334L + 30_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Thu hoan ship khi co replacement",
            returnRequestId = s2Id,
            refundedAt = "2026-11-25T05:12:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(shipWithReplacement, HttpStatusCode.UnprocessableEntity, "shipping_refund_requires_full_return");

        // s2 goods-only at exact cumulative cap 233_334 succeeds!
        using (var ok700_2 = await PostIdempotentJsonAsync(app.Client, path700, new
        {
            kind = "return",
            amountVnd = 233_334L,
            method = "bank_transfer",
            reason = "Hoan 233334",
            returnRequestId = s2Id,
            refundedAt = "2026-11-25T05:13:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, ok700_2.StatusCode);

        // === Scenario 3: Order with a 0-VND line (paid 500k + gift 0d + shipping 30k = 530k) ===
        var (_, _, giftVariantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 50_000L, suffix: "zln");
        OrderSummary giftOrder;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrder, OrderSummary>>();
            var res = await create.Handle(new CreateAdminOrder(
                SalesChannel.ZaloPersonal,
                customerId,
                app.OwnerId,
                true,
                OrderPaymentMethod.BankTransfer,
                SampleContact,
                [new(variantId, 1, 500_000L, "Gia rieng"), new(giftVariantId, 1, 0L, "Qua tang 0d")],
                null,
                null,
                null,
                null,
                30_000L,
                SampleAttribution,
                null,
                null), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            giftOrder = res.Value;
        }
        Assert.Equal(530_000L, giftOrder.TotalVnd);

        using (var payGift = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{giftOrder.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 530_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, payGift.StatusCode);

        var paidLineId = giftOrder.Lines.Single(l => l.UnitPriceVnd == 500_000L).Id;
        var zeroLineId = giftOrder.Lines.Single(l => l.UnitPriceVnd == 0L).Id;
        var retPaidOnlyId = Guid.CreateVersion7();
        var retZeroLineId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{giftOrder.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES
  ('{retPaidOnlyId}','DT-261126-310','{giftOrder.Id}','defective','Tra dong 500k','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{retZeroLineId}','DT-261126-311','{giftOrder.Id}','defective','Tra not dong qua tang 0d','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES
  ('{Guid.CreateVersion7()}','{retPaidOnlyId}','{paidLineId}',1,false),
  ('{Guid.CreateVersion7()}','{retZeroLineId}','{zeroLineId}',1,false);
""");

        var pathGift = $"/api/v1/admin/orders/{giftOrder.Id}/refunds";

        // Returning ONLY the 500k line leaves the 0-VND line unreturned -> shipping refund MUST be blocked!
        using (var shipBlockedOnGift = await PostIdempotentJsonAsync(app.Client, pathGift, new
        {
            kind = "return",
            amountVnd = 530_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Thu hoan ship khi chua tra dong 0d",
            returnRequestId = retPaidOnlyId,
            refundedAt = "2026-11-25T05:15:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(shipBlockedOnGift, HttpStatusCode.UnprocessableEntity, "shipping_refund_requires_full_return");

        // Goods-only 500k succeeds, then returning the 0-VND line completes 100% of every line and unlocks 30k shipping!
        using (var okPaidLine = await PostIdempotentJsonAsync(app.Client, pathGift, new
        {
            kind = "return",
            amountVnd = 500_000L,
            method = "bank_transfer",
            reason = "Hoan dong 500k",
            returnRequestId = retPaidOnlyId,
            refundedAt = "2026-11-25T05:16:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, okPaidLine.StatusCode);

        using (var okZeroLineShip = await PostIdempotentJsonAsync(app.Client, pathGift, new
        {
            kind = "return",
            amountVnd = 30_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Tra not dong 0d -> hoan phi ship 30k",
            returnRequestId = retZeroLineId,
            refundedAt = "2026-11-25T05:17:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, okZeroLineShip.StatusCode);
            var dto = (await okZeroLineShip.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(30_000L, dto.ShippingAmountVnd);
            Assert.Equal(OrderPaymentStatus.Refunded, dto.OrderPaymentStatus);
            Assert.Equal(0L, dto.NetPaidVnd);
            Assert.False(dto.NeedsRefund);
        }
    }

    [Fact]
    public async Task O3bShippingDoubleRefundBlockedAcrossAdjustmentAndReturnAndSavepointCatchesDuplicateReturnRefund()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "dbl");

        // Order: 500k goods + 30k shipping = 530k, delivered & overpaid 550k (so currentNet stays >= 420k after 130k adjustment,
        // allowing goods/shipping ceiling checks to run before refund_exceeds_net_paid).
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 30_000L);
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 550_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        var fullReturnId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{order.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES ('{fullReturnId}','DT-261126-401','{order.Id}','defective','Tra toan bo','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{fullReturnId}','{order.Lines[0].Id}',1,false);
""");

        var path = $"/api/v1/admin/orders/{order.Id}/refunds";

        // 1. Adjustment refunds 100k goods + 30k shipping = 130k (currentNet = 420k, remaining goods = 400k, remaining ship = 0).
        using (var adj = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "adjustment",
            amountVnd = 130_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Ho tro 100k + mien phi ship 30k",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, adj.StatusCode);

        // 2. Full return attempting to refund 30k shipping a SECOND time -> 422 refund_exceeds_shipping_fee!
        using (var dupShip = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "return",
            amountVnd = 400_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            reason = "Thu hoan ship lan 2",
            returnRequestId = fullReturnId,
            refundedAt = "2026-11-25T05:06:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(dupShip, HttpStatusCode.UnprocessableEntity, "refund_exceeds_shipping_fee");

        // 2b. Adjustment attempting 400_001 goods (remaining merchandise after 100k adjustment is 400k) -> 422 refund_exceeds_order_merchandise!
        using (var adjOverMerch = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "adjustment",
            amountVnd = 400_001L,
            shippingAmountVnd = 0L,
            method = "bank_transfer",
            reason = "Thu adjustment vuot tien hang",
            refundedAt = "2026-11-25T05:06:30Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(adjOverMerch, HttpStatusCode.UnprocessableEntity, "refund_exceeds_order_merchandise");

        // 3. Full return attempting 400_001 goods (remaining merchandise after 100k adjustment is 400k) -> 422 refund_exceeds_return_lines!
        using (var overMerch = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "return",
            amountVnd = 400_001L,
            shippingAmountVnd = 0L,
            method = "bank_transfer",
            reason = "Thu vuot phan hang con lai",
            returnRequestId = fullReturnId,
            refundedAt = "2026-11-25T05:07:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(overMerch, HttpStatusCode.UnprocessableEntity, "refund_exceeds_return_lines");

        // 4. Full return with exact remaining 400k goods + 0 shipping succeeds (20k overpayment remains -> Overpaid = 20k, NeedsRefund = true).
        using (var okRet = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "return",
            amountVnd = 400_000L,
            shippingAmountVnd = 0L,
            method = "bank_transfer",
            reason = "Hoan not 400k tien hang",
            returnRequestId = fullReturnId,
            refundedAt = "2026-11-25T05:08:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, okRet.StatusCode);
            var dto = (await okRet.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.PartiallyRefunded, dto.OrderPaymentStatus);
            Assert.Equal(20_000L, dto.NetPaidVnd);
            Assert.True(dto.IsOverpaid);
            Assert.True(dto.NeedsRefund);
        }

        // 4b. Refund remaining 20k overpayment -> net = 0, payment_status = 'refunded', IsOverpaid = false, NeedsRefund = false.
        using (var okOver = await PostIdempotentJsonAsync(app.Client, path, new
        {
            kind = "overpayment",
            amountVnd = 20_000L,
            method = "bank_transfer",
            reason = "Hoan not 20k tien thua",
            refundedAt = "2026-11-25T05:08:30Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, okOver.StatusCode);
            var dto = (await okOver.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.Refunded, dto.OrderPaymentStatus);
            Assert.Equal(0L, dto.NetPaidVnd);
            Assert.False(dto.IsOverpaid);
            Assert.False(dto.NeedsRefund);
        }

        // 5. Probe SAVEPOINT 23505 ux_refunds_return_request -> 409 return_already_refunded!
        //    Simulate a second order where an approved return_request already has a committed refund row.
        var orderSp = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);
        using (var paySp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{orderSp.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, paySp.StatusCode);

        var dupReturnId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{orderSp.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES ('{dupReturnId}','DT-261126-402','{orderSp.Id}','defective','Test savepoint','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{dupReturnId}','{orderSp.Lines[0].Id}',1,false);
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES ('{Guid.CreateVersion7()}','{orderSp.Id}','{dupReturnId}','return',100000,0,'cash','Existing refund',now(),'{app.OwnerId}');
""");

        using var spResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{orderSp.Id}/refunds", new
        {
            kind = "return",
            amountVnd = 100_000L,
            method = "cash",
            reason = "Trigger savepoint 23505",
            returnRequestId = dupReturnId,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(spResp, HttpStatusCode.Conflict, "return_already_refunded");
    }

    [Fact]
    public async Task O3bConcurrentReturnRefundsForSameReturnRequestCreateExactlyOneRefund()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "rrace");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);

        var returnId = Guid.CreateVersion7();
        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{order.Id}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES ('{returnId}','DT-261126-499','{order.Id}','defective','Race return','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{returnId}','{order.Lines[0].Id}',1,false);
""");

        const int count = 6;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = $"/api/v1/admin/orders/{order.Id}/refunds";

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await barrier.Task;
            using var resp = await PostIdempotentJsonAsync(app.Client, path, new
            {
                kind = "return",
                amountVnd = 200_000L,
                method = "bank_transfer",
                reason = $"Hoan race #{i}",
                returnRequestId = returnId,
                refundedAt = "2026-11-25T05:20:00Z"
            }, Guid.CreateVersion7().ToString("D"));
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, body);
        }).ToArray();

        barrier.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(count - 1, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var (_, body) in results.Where(x => x.StatusCode == HttpStatusCode.Conflict))
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.GetProperty("code").GetString();
            Assert.True(code is "invalid_return_status" or "return_already_refunded", $"Unexpected conflict code: {code}");
        }

        await using var db = app.OwnerContext();
        Assert.Equal(1, await db.Set<Refund>().AsNoTracking().CountAsync(x => x.ReturnRequestId == returnId));
        var rr = await db.Set<ReturnRequest>().AsNoTracking().SingleAsync(x => x.Id == returnId);
        Assert.Equal(ReturnRequestStatus.Resolved, rr.Status);
    }

    [Fact]
    public async Task G04G29BrRet0203RefundWithReturnRequestIdValidatesOrderStatusLineCapAndResolvesAtomically()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 300_000L, suffix: "ret");

        // Order A: qty 3 x 300k = 900k subtotal, 100k manual discount, 0 shipping -> total 800k.
        var orderA = await CreateAdminOrderAsync(
            app, customerId, variantId, quantity: 3,
            manualDiscountVnd: 100_000L, discountNote: "Giam 100k", overrideShippingFeeVnd: 0L);
        var orderB = await CreateAdminOrderAsync(app, customerId, variantId, quantity: 1, overrideShippingFeeVnd: 0L);

        using (var payA = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{orderA.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 800_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            Assert.Equal(HttpStatusCode.OK, payA.StatusCode);

        // Seed 4 return requests on delivered orderA: wrongOrder, requested, replacement, approvedRefund (qty 1 of 3 -> cap 266_667).
        var wrongOrderReturnId = Guid.CreateVersion7();
        var requestedReturnId = Guid.CreateVersion7();
        var replacementReturnId = Guid.CreateVersion7();
        var approvedRefundReturnId = Guid.CreateVersion7();
        var orderALineId = orderA.Lines[0].Id;

        await app.ExecuteAsync($"""
UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{orderA.Id}';
UPDATE sales.order_lines SET returned_quantity = 1 WHERE id = '{orderALineId}';
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,decided_by,decided_at,created_by)
VALUES
  ('{wrongOrderReturnId}','DT-261125-001','{orderB.Id}','defective','Loi don B','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{requestedReturnId}','DT-261125-002','{orderA.Id}','defective','Chua duyet','requested',NULL,now(),NULL,NULL,'{app.OwnerId}'),
  ('{replacementReturnId}','DT-261125-003','{orderA.Id}','damaged_in_transit','Doi hang','approved','replacement',now(),'{app.OwnerId}',now(),'{app.OwnerId}'),
  ('{approvedRefundReturnId}','DT-261125-004','{orderA.Id}','damaged_in_transit','Vo 1 san pham','approved','refund',now(),'{app.OwnerId}',now(),'{app.OwnerId}');
INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable)
VALUES ('{Guid.CreateVersion7()}','{approvedRefundReturnId}','{orderALineId}',1,false);
""");

        var refundPath = $"/api/v1/admin/orders/{orderA.Id}/refunds";

        // (a0) kind=return without returnRequestId -> 422 return_request_required;
        //      kind=adjustment with returnRequestId -> 422 return_request_not_allowed.
        using (var missingRetId = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Thieu phieu doi tra",
            refundedAt = "2026-11-25T05:09:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(missingRetId, HttpStatusCode.UnprocessableEntity, "return_request_required");

        using (var unexpectedRetId = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "adjustment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Adjustment khong duoc gan returnRequestId",
            returnRequestId = approvedRefundReturnId,
            refundedAt = "2026-11-25T05:09:30Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(unexpectedRetId, HttpStatusCode.UnprocessableEntity, "return_request_not_allowed");

        // (a) Wrong order -> 422 return_request_order_mismatch.
        using (var wrong = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Sai don",
            returnRequestId = wrongOrderReturnId,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "return_request_order_mismatch");

        // (b) Not approved or replacement -> 409 invalid_return_status.
        using (var req = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Chua duyet",
            returnRequestId = requestedReturnId,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(req, HttpStatusCode.Conflict, "invalid_return_status");

        using (var rep = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "La replacement",
            returnRequestId = replacementReturnId,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(rep, HttpStatusCode.Conflict, "invalid_return_status");

        // (c) Exceeds BR-RET-02 proportional cap (266_668 > 266_667) -> 422 refund_exceeds_return_lines.
        using (var overCap = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 266_668L,
            method = "bank_transfer",
            reason = "Vuot tran dong tra",
            returnRequestId = approvedRefundReturnId,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(overCap, HttpStatusCode.UnprocessableEntity, "refund_exceeds_return_lines");

        // (d) Valid refund at exact cap 266_667 -> 200, resolves return_request atomically + audits both refund & return_request!
        using var ok = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 266_667L,
            method = "bank_transfer",
            bankReference = "FT-REFUND-RET-004",
            reason = "Hoan tien theo phieu doi tra DT-261125-004",
            returnRequestId = approvedRefundReturnId,
            refundedAt = "2026-11-25T05:15:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var dto = (await ok.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
        Assert.Equal(RefundKind.Return, dto.Kind);
        Assert.Equal(0L, dto.ShippingAmountVnd);
        Assert.Equal(approvedRefundReturnId, dto.ReturnRequestId);
        Assert.Equal(ReturnRequestStatus.Resolved, dto.ReturnRequestStatus);
        Assert.Equal(OrderPaymentStatus.PartiallyRefunded, dto.OrderPaymentStatus);
        Assert.Equal(800_000L - 266_667L, dto.NetPaidVnd);

        await using (var db = app.OwnerContext())
        {
            var rr = await db.Set<ReturnRequest>().AsNoTracking().SingleAsync(x => x.Id == approvedRefundReturnId);
            Assert.Equal(ReturnRequestStatus.Resolved, rr.Status);
            Assert.NotNull(rr.ResolvedAt);

            var refundRow = await db.Set<Refund>().AsNoTracking().SingleAsync(x => x.Id == dto.RefundId);
            Assert.Equal(approvedRefundReturnId, refundRow.ReturnRequestId);
            Assert.Equal(RefundKind.Return, refundRow.Kind);
            Assert.Equal(0L, refundRow.ShippingAmountVnd);

            Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(x =>
                x.EntityType == "sales.return_requests" && x.EntityId == approvedRefundReturnId && x.Action == "update"));
        }

        // (e) Second refund targeting already resolved return request -> 409 invalid_return_status.
        using (var again = await PostIdempotentJsonAsync(app.Client, refundPath, new
        {
            kind = "return",
            amountVnd = 10_000L,
            method = "cash",
            reason = "Thu hoan lan 2 cho cung phieu",
            returnRequestId = approvedRefundReturnId,
            refundedAt = "2026-11-25T05:20:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(again, HttpStatusCode.Conflict, "invalid_return_status");
    }

    [Fact]
    public async Task BrOrd08BrPay02UpdateOrderLinesRecalculatesPaymentStatusWithoutImplicitAutoConfirm()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 100_000L, suffix: "u1");
        var order = await CreateWebBankOrderAsync(app, customerId, variantId, quantity: 5, paymentMethod: OrderPaymentMethod.BankTransfer);
        Assert.Equal(530_000L, order.TotalVnd);

        // Pay 330k -> partially_paid, pending.
        using (var pay = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 330_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
            var dto = (await pay.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
            Assert.Equal(OrderPaymentStatus.PartiallyPaid, dto.OrderPaymentStatus);
        }

        // Reduce lines to 3 x 100k + 30k shipping = 330k -> payment_status auto-updates to 'paid', stays 'pending' (no implicit auto-confirm).
        uint currentVersion;
        await using (var db = app.OwnerContext())
            currentVersion = (await db.Set<PersistenceOrder>().AsNoTracking().SingleAsync(x => x.Id == order.Id)).Version;

        OrderSummary updated1;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var res = await update.Handle(new UpdateOrderLines(
                order.Id, currentVersion, true, [new(variantId, 3)], null, null, null, null, 30_000L), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            updated1 = res.Value;
        }
        Assert.Equal(330_000L, updated1.TotalVnd);
        Assert.Equal(OrderPaymentStatus.Paid, updated1.PaymentStatus);
        Assert.Equal(OrderStatus.Pending, updated1.Status);

        // Reduce further to 2 x 100k + 30k = 230k -> payment_status stays 'paid', calculator reports Overpaid = 100k.
        OrderSummary updated2;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var update = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateOrderLines, OrderSummary>>();
            var res = await update.Handle(new UpdateOrderLines(
                order.Id, updated1.Version, true, [new(variantId, 2)], null, null, null, null, 30_000L), CancellationToken.None);
            Assert.True(res.IsSuccess, res.IsSuccess ? "" : res.Error.Code);
            updated2 = res.Value;
        }
        Assert.Equal(230_000L, updated2.TotalVnd);
        Assert.Equal(OrderPaymentStatus.Paid, updated2.PaymentStatus);
        var calc = OrderPaymentStatusCalculator.Calculate(330_000L, 0L, 0L, updated2.TotalVnd, updated2.Status, false);
        Assert.True(calc.IsOverpaid);
        Assert.Equal(100_000L, calc.OverpaidVnd);
    }

    [Fact]
    public async Task Rp20106AuditRedactionIdempotencySafetyAndProtectedDeletesUnderTpApp()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "sec");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 30_000L);

        const string secretPayRef = "FT-SECRET-PAY-0901112222";
        const string secretPayNote = "Ghi chu bi mat Nguyen Van Pay";
        const string secretRefundRef = "FT-SECRET-REFUND-0903334444";
        const string secretRefundReason = "Ly do bi mat Tran Thi Refund";

        using var payResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 530_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = secretPayRef,
            note = secretPayNote
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, payResp.StatusCode);
        var payDto = (await payResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;

        await app.ExecuteAsync($"UPDATE sales.orders SET status='delivered', delivered_at=now() WHERE id='{order.Id}'");

        using var refundResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "adjustment",
            amountVnd = 200_000L,
            shippingAmountVnd = 30_000L,
            method = "bank_transfer",
            bankReference = secretRefundRef,
            reason = secretRefundReason,
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, refundResp.StatusCode);
        var refundDto = (await refundResp.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;

        // 1. Audit logs redact all sensitive fields on sales.payments and sales.refunds, and allowlist kind + shipping_amount_vnd.
        await using (var db = app.OwnerContext())
        {
            var paymentAudit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(x => x.EntityType == "sales.payments" && x.EntityId == payDto.PaymentId && x.Action == "create");
            Assert.DoesNotContain(secretPayRef, paymentAudit.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPayNote, paymentAudit.Changes, StringComparison.Ordinal);
            using (var doc = JsonDocument.Parse(paymentAudit.Changes))
            {
                Assert.Equal("[redacted]", doc.RootElement.GetProperty("bank_reference").GetProperty("after").GetString());
                Assert.Equal("[redacted]", doc.RootElement.GetProperty("note").GetProperty("after").GetString());
                Assert.Equal("manual", doc.RootElement.GetProperty("provider").GetProperty("after").GetString());
                Assert.Equal(530_000L, doc.RootElement.GetProperty("amount_vnd").GetProperty("after").GetInt64());
            }

            var refundAudit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(x => x.EntityType == "sales.refunds" && x.EntityId == refundDto.RefundId && x.Action == "create");
            Assert.DoesNotContain(secretRefundRef, refundAudit.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain(secretRefundReason, refundAudit.Changes, StringComparison.Ordinal);
            using (var doc = JsonDocument.Parse(refundAudit.Changes))
            {
                Assert.Equal("[redacted]", doc.RootElement.GetProperty("bank_reference").GetProperty("after").GetString());
                Assert.Equal("[redacted]", doc.RootElement.GetProperty("reason").GetProperty("after").GetString());
                Assert.Equal("adjustment", doc.RootElement.GetProperty("kind").GetProperty("after").GetString());
                Assert.Equal(200_000L, doc.RootElement.GetProperty("amount_vnd").GetProperty("after").GetInt64());
                Assert.Equal(30_000L, doc.RootElement.GetProperty("shipping_amount_vnd").GetProperty("after").GetInt64());
            }

            // 2. Decoded platform.idempotency_keys.response_body contains zero sensitive strings.
            var idemRows = await db.IdempotencyKeys.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, idemRows.Length);
            foreach (var row in idemRows)
            {
                using var env = JsonDocument.Parse(row.ResponseBody!);
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(env.RootElement.GetProperty("bodyBase64").GetString()!));
                Assert.DoesNotContain(secretPayRef, decoded, StringComparison.Ordinal);
                Assert.DoesNotContain(secretPayNote, decoded, StringComparison.Ordinal);
                Assert.DoesNotContain(secretRefundRef, decoded, StringComparison.Ordinal);
                Assert.DoesNotContain(secretRefundReason, decoded, StringComparison.Ordinal);
            }
        }

        // 3. Reflection check: IRefundRepository & EfRefundRepository are strictly append-only (no update/delete methods).
        var refundPortMethods = typeof(IRefundRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToArray();
        Assert.DoesNotContain(refundPortMethods, n => n.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Void", StringComparison.OrdinalIgnoreCase));

        var refundImplMethods = typeof(EfRefundRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToArray();
        Assert.DoesNotContain(refundImplMethods, n => n.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Void", StringComparison.OrdinalIgnoreCase));

        // 4. Runtime tp_app role is blocked by PostgreSQL 42501 on DELETE FROM sales.payments / sales.refunds.
        await using var runtimeConn = new NpgsqlConnection(app.RuntimeConnection);
        await runtimeConn.OpenAsync();
        foreach (var sql in new[]
        {
            $"DELETE FROM sales.payments WHERE id = '{payDto.PaymentId}'",
            $"DELETE FROM sales.refunds WHERE id = '{refundDto.RefundId}'"
        })
        {
            await using var cmd = new NpgsqlCommand(sql, runtimeConn);
            var pgEx = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", pgEx.SqlState);
        }
    }
}
