using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TeaPremium.Application.Payments;
using TeaPremium.Domain.Orders;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class PaymentAuthorizationAndCsrfTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AnonymousHttpMissingCsrfAndUnsupportedContentTypeRejectedOnPaymentEndpoints()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        var fakeOrderId = Guid.CreateVersion7();
        var fakePaymentId = Guid.CreateVersion7();

        // 1. Plain HTTP rejected with 400 https_required.
        using (var httpClient = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("http://localhost"),
            AllowAutoRedirect = false
        }))
        {
            using var resp = await httpClient.GetAsync("/api/v1/admin/payments/awaiting?type=cod");
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "https_required");
        }

        // 2. Unauthenticated HTTPS rejected with 401 unauthenticated on all 4 endpoints.
        using (var get = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=cod"))
            await AssertProblemAsync(get, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var p1 = await app.Client.PostAsJsonAsync($"/api/v1/admin/orders/{fakeOrderId}/payments", new { }))
            await AssertProblemAsync(p1, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var p2 = await app.Client.PostAsJsonAsync($"/api/v1/admin/payments/{fakePaymentId}/void", new { reason = "x" }))
            await AssertProblemAsync(p2, HttpStatusCode.Unauthorized, "unauthenticated");
        using (var p3 = await app.Client.PostAsJsonAsync($"/api/v1/admin/orders/{fakeOrderId}/refunds", new { }))
            await AssertProblemAsync(p3, HttpStatusCode.Unauthorized, "unauthenticated");

        // 3. Authenticated owner without X-CSRF-TOKEN rejected with 400 csrf_invalid on all 3 POST endpoints.
        await app.LoginAsync();
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{fakeOrderId}/payments") { Content = JsonContent.Create(new { }) })
        {
            req.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
            using var r = await app.Client.SendAsync(req);
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "csrf_invalid");
        }
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/payments/{fakePaymentId}/void") { Content = JsonContent.Create(new { reason = "x" }) })
        {
            using var r = await app.Client.SendAsync(req);
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "csrf_invalid");
        }
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{fakeOrderId}/refunds") { Content = JsonContent.Create(new { }) })
        {
            req.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
            using var r = await app.Client.SendAsync(req);
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "csrf_invalid");
        }

        // 4. Wrong Content-Type (text/plain) returns RFC 9457 415 unsupported_media_type on all 3 POST endpoints.
        var csrf = await GetCsrfTokenAsync(app.Client);
        foreach (var path in new[]
        {
            $"/api/v1/admin/orders/{fakeOrderId}/payments",
            $"/api/v1/admin/payments/{fakePaymentId}/void",
            $"/api/v1/admin/orders/{fakeOrderId}/refunds"
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent("plain text", Encoding.UTF8, "text/plain")
            };
            req.Headers.Add("X-CSRF-TOKEN", csrf);
            req.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
            using var resp = await app.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }
    }

    [Fact]
    public async Task ContentRoleForbiddenOnAllFourPaymentEndpoints()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var contentEmail = await CreateStaffAsync(app, "content");
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "authc");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        using var recorded = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 500_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        var payment = (await recorded.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;

        await app.LoginAsync(contentEmail, StaffNewPassword);

        using (var r1 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "cash",
            amountVnd = 100_000L,
            receivedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(r1, HttpStatusCode.Forbidden, "forbidden");

        using (var r2 = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{payment.PaymentId}/void", new { reason = "Content void" }))
            await AssertProblemAsync(r2, HttpStatusCode.Forbidden, "forbidden");

        using (var r3 = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer"))
            await AssertProblemAsync(r3, HttpStatusCode.Forbidden, "forbidden");

        using (var r4 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "cash",
            reason = "Content refund",
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(r4, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task SalesRoleAllowedOnPaymentsAndAwaitingForbidden403OnVoidAndRefunds()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var salesEmail = await CreateStaffAsync(app, "sales");
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "auths");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        await app.LoginAsync(salesEmail, StaffNewPassword);

        // Sales CAN record payment (payments.write) -> 200.
        using var recResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 300_000L,
            receivedAt = "2026-11-25T05:00:00Z",
            bankReference = "FT-SALES-001"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, recResp.StatusCode);
        var payment = (await recResp.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;
        Assert.Equal(OrderPaymentStatus.PartiallyPaid, payment.OrderPaymentStatus);

        // Sales CAN read awaiting payments (payments.write) -> 200.
        using (var awaiting = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer"))
            Assert.Equal(HttpStatusCode.OK, awaiting.StatusCode);

        // Sales CANNOT void payment (owner only) -> 403 forbidden.
        using (var voidResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{payment.PaymentId}/void", new { reason = "Sales void" }))
            await AssertProblemAsync(voidResp, HttpStatusCode.Forbidden, "forbidden");

        // Sales CANNOT create refund (refunds.write = owner only) -> 403 forbidden.
        using (var refundResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Sales refund",
            refundedAt = "2026-11-25T05:10:00Z"
        }, Guid.CreateVersion7().ToString("D")))
            await AssertProblemAsync(refundResp, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task OwnerRoleAllowedOnAllFourPaymentEndpoints()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await app.LoginAsync();
        var (customerId, _, variantId, _) = await SeedProductVariantAndCustomerAsync(app, false, onHand: 10, priceVnd: 500_000L, suffix: "autho");
        var order = await CreateAdminOrderAsync(app, customerId, variantId, overrideShippingFeeVnd: 0L);

        // 1. GET awaiting -> 200.
        using (var awaiting = await app.Client.GetAsync("/api/v1/admin/payments/awaiting?type=bank_transfer"))
            Assert.Equal(HttpStatusCode.OK, awaiting.StatusCode);

        // 2. POST payments -> 200 (two payments: one to void, one overpaying 600k on 500k order).
        using var rec1 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "bank_transfer",
            amountVnd = 200_000L,
            receivedAt = "2026-11-25T05:00:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, rec1.StatusCode);
        var p1 = (await rec1.Content.ReadFromJsonAsync<PaymentRecordedView>(WebJson))!;

        using var rec2 = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/payments", new
        {
            method = "cash",
            amountVnd = 600_000L,
            receivedAt = "2026-11-25T05:01:00Z"
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, rec2.StatusCode);

        // 3. POST void -> 200.
        using (var voidResp = await app.MutateAsync(HttpMethod.Post, $"/api/v1/admin/payments/{p1.PaymentId}/void", new { reason = "Owner huy khoan thu thua" }))
            Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);

        // 4. POST refunds (kind=overpayment 100k) -> 200.
        using (var refundResp = await PostIdempotentJsonAsync(app.Client, $"/api/v1/admin/orders/{order.Id}/refunds", new
        {
            kind = "overpayment",
            amountVnd = 100_000L,
            method = "bank_transfer",
            reason = "Owner hoan tien thu thua",
            refundedAt = "2026-11-25T05:05:00Z"
        }, Guid.CreateVersion7().ToString("D")))
        {
            Assert.Equal(HttpStatusCode.OK, refundResp.StatusCode);
            var dto = (await refundResp.Content.ReadFromJsonAsync<RefundRecordedView>(WebJson))!;
            Assert.Equal(RefundKind.Overpayment, dto.Kind);
            Assert.Equal(0L, dto.ShippingAmountVnd);
            Assert.Equal(OrderPaymentStatus.Paid, dto.OrderPaymentStatus);
        }
    }
}
