using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class AdminOrdersPricingSecurityAndBackfillTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string OrdersRoot = "/api/v1/admin/orders";
    private const string AdminCsv = "province_code,province_name,ward_code,ward_name\n"
        + "79,TP Hồ Chí Minh,,\n"
        + "79,TP Hồ Chí Minh,79001,Phường 1\n"
        + "01,Hà Nội,,\n"
        + "01,Hà Nội,00001,Phường Ba Đình\n";

    [Fact]
    public async Task ManualDiscount15PercentSalesReceives422DiscountRequiresOwnerOwnerSucceedsAndSalesCanEditShipping()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        var customerId = await CreateHttpCustomerAsync(h, "Khách Giảm Giá", "0903111001");
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "disc1");

        // Login as sales (lacks costs.read; orders.max_staff_discount_percent default = 10%)
        await h.LoginAsync(salesEmail, StaffNewPassword);

        // Missing discountNote when discountVnd > 0 -> 400 validation_failed
        using var missingNote = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            discountVnd = 50_000L,
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(missingNote, HttpStatusCode.BadRequest, "validation_failed");

        // Sales 15% discount (150_000 on 1_000_000) -> 422 discount_requires_owner
        using var sales15 = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            discountVnd = 150_000L,
            discountNote = "Giảm 15%",
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(sales15, HttpStatusCode.UnprocessableEntity, "discount_requires_owner");

        // Sales 10% discount (100_000 on 1_000_000) -> 201 Created
        using var sales10 = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            discountVnd = 100_000L,
            discountNote = "Giảm 10% trong ngưỡng",
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, sales10.StatusCode);
        var salesOrder = (await sales10.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(100_000L, salesOrder.DiscountVnd);

        // Sales PATCH attempting 15% -> 422 discount_requires_owner
        using var salesPatch15 = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{salesOrder.Id}", new
        {
            rowVersion = salesOrder.RowVersion,
            discountVnd = 150_000L,
            discountNote = "Tăng lên 15%"
        });
        await AssertProblemAsync(salesPatch15, HttpStatusCode.UnprocessableEntity, "discount_requires_owner");

        // Owner creates order with 15% discount -> 201 Created
        await h.LoginAsync();
        using var owner15 = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "zalo_personal",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            discountVnd = 150_000L,
            discountNote = "Owner duyệt giảm 15%",
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, owner15.StatusCode);
        var ownerOrder = (await owner15.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(150_000L, ownerOrder.DiscountVnd);

        // ASSUMPTION-P206-01 = A: Sales CAN edit shipping & internalNote on owner-approved 15% order without touching lines/discount
        await h.LoginAsync(salesEmail, StaffNewPassword);
        using var salesEditShipping = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{ownerOrder.Id}", new
        {
            rowVersion = ownerOrder.RowVersion,
            shipping = new { addressLine = "99 Nguyễn Huệ" },
            internalNote = "Gọi khách trước 30 phút"
        });
        Assert.Equal(HttpStatusCode.OK, salesEditShipping.StatusCode);
        var editedDetail = (await salesEditShipping.Content.ReadFromJsonAsync<AdminOrderDetailView>())!;
        Assert.Equal("99 Nguyễn Huệ", editedDetail.AddressLine);
        Assert.Equal("Gọi khách trước 30 phút", editedDetail.InternalNote);
        Assert.Equal(150_000L, editedDetail.DiscountVnd);

        // But if sales shrinks lines so existing 150_000 manual discount becomes 30% -> 422 discount_requires_owner
        using var salesShrinkLines = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{ownerOrder.Id}", new
        {
            rowVersion = editedDetail.RowVersion,
            lines = new[] { new { variantId, quantity = 1 } }
        });
        await AssertProblemAsync(salesShrinkLines, HttpStatusCode.UnprocessableEntity, "discount_requires_owner");
    }

    [Fact]
    public async Task PriceOverrideBelowAvgCostSalesReceives422ZeroCostLeakageOwnerSucceedsAndUnitCostFiltered()
    {
        var logs = new ConcurrentQueue<string>();
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddLogging(lb => lb.AddProvider(new CapturingLoggerProvider(logs)));
        });
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        var customerId = await CreateHttpCustomerAsync(h, "Khách Giá Vốn", "0903111002");
        const long secretAvgCost = 317_500L;
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 500_000L, avgCostVnd: secretAvgCost, suffix: "cost1");

        await h.LoginAsync(salesEmail, StaffNewPassword);

        // Short priceOverrideReason (2 chars) -> 400 validation_failed
        using var shortReason = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 400_000L, priceOverrideReason = "ab" } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(shortReason, HttpStatusCode.BadRequest, "validation_failed");

        // Sales override BELOW avg_cost_vnd on POST -> 422 price_below_allowed with ZERO cost leakage in JSON or logs
        using var belowPost = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 300_000L, priceOverrideReason = "Giá ưu đãi" } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, belowPost.StatusCode);
        var belowPostRaw = await belowPost.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(belowPostRaw))
        {
            Assert.Equal("price_below_allowed", doc.RootElement.GetProperty("code").GetString());
            Assert.Equal(variantId, doc.RootElement.GetProperty("data").GetProperty("variantId").GetGuid());
        }
        AssertNoCostLeakage(belowPostRaw, secretAvgCost);

        // Sales override AT/ABOVE avg_cost_vnd on POST -> 201 Created
        using var abovePost = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "phone",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 320_000L, priceOverrideReason = "Khách quen" } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, abovePost.StatusCode);
        var salesCreated = (await abovePost.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        // Sales override BELOW avg_cost_vnd on PATCH -> 422 price_below_allowed with ZERO cost leakage
        using var belowPatch = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{salesCreated.Id}", new
        {
            rowVersion = salesCreated.RowVersion,
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 290_000L, priceOverrideReason = "Thử hạ giá" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, belowPatch.StatusCode);
        var belowPatchRaw = await belowPatch.Content.ReadAsStringAsync();
        Assert.Contains("price_below_allowed", belowPatchRaw, StringComparison.Ordinal);
        AssertNoCostLeakage(belowPatchRaw, secretAvgCost);

        // Verify captured logs contain zero cost leakage
        var allLogs = string.Join("\n", logs);
        Assert.DoesNotContain("317500", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("317,500", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("317.500", allLogs, StringComparison.Ordinal);

        // Owner override BELOW avg_cost_vnd succeeds on POST
        await h.LoginAsync();
        using var ownerBelow = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1, unitPriceVnd = 250_000L, priceOverrideReason = "Owner bán thanh lý" } },
            shipping = DefaultShipping()
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, ownerBelow.StatusCode);
        var ownerCreated = (await ownerBelow.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;

        // Lock unit_cost_vnd on order_lines (simulating post-shipment cost snapshot)
        await h.ExecuteAsync($"UPDATE sales.order_lines SET unit_cost_vnd = {secretAvgCost} WHERE order_id = '{ownerCreated.Id}'");

        // Owner sees unitCostVnd on GET detail
        using var ownerGet = await h.Client.GetAsync($"{OrdersRoot}/{ownerCreated.Id}");
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
        var ownerGetRaw = await ownerGet.Content.ReadAsStringAsync();
        Assert.Contains("\"unitCostVnd\":317500", ownerGetRaw, StringComparison.Ordinal);

        // Sales NEVER sees unitCostVnd on GET detail, GET list, PATCH, confirm, or cancel
        await h.LoginAsync(salesEmail, StaffNewPassword);
        using var salesGet = await h.Client.GetAsync($"{OrdersRoot}/{ownerCreated.Id}");
        Assert.Equal(HttpStatusCode.OK, salesGet.StatusCode);
        var salesGetRaw = await salesGet.Content.ReadAsStringAsync();
        Assert.DoesNotContain("unitCostVnd", salesGetRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("317500", salesGetRaw, StringComparison.Ordinal);

        using var salesList = await h.Client.GetAsync(OrdersRoot);
        Assert.Equal(HttpStatusCode.OK, salesList.StatusCode);
        var salesListRaw = await salesList.Content.ReadAsStringAsync();
        Assert.DoesNotContain("unitCostVnd", salesListRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("avgCostVnd", salesListRaw, StringComparison.OrdinalIgnoreCase);

        using var salesPatchNote = await h.MutateAsync(HttpMethod.Patch, $"{OrdersRoot}/{ownerCreated.Id}", new
        {
            rowVersion = ownerCreated.RowVersion,
            internalNote = "Kiểm tra không lộ giá vốn"
        });
        Assert.Equal(HttpStatusCode.OK, salesPatchNote.StatusCode);
        var salesPatchRaw = await salesPatchNote.Content.ReadAsStringAsync();
        Assert.DoesNotContain("unitCostVnd", salesPatchRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("317500", salesPatchRaw, StringComparison.Ordinal);

        // Staff lacking orders.override_price attempting unitPriceVnd override on Create or Patch -> 403 forbidden
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var createHandler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateAdminOrderHttp, AdminOrderCreatedView>>();
        var patchHandler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PatchAdminOrder, AdminOrderDetailView>>();
        var noOverrideActor = new OrderActor(Guid.CreateVersion7(), IsOwner: false, HasOverridePrice: false, HasCostsRead: false);

        var createForbidden = await createHandler.Handle(new CreateAdminOrderHttp(
            customerId,
            null,
            SalesChannel.Phone,
            OrderPaymentMethod.Cod,
            [new AdminOrderLineInput(variantId, 1, 450_000L, "Thử sửa giá không có quyền")],
            null,
            null,
            null,
            new AdminOrderShippingInput("A", "0909123456", "79", "79001", null, "10 Lê Lợi", null),
            null,
            false,
            false,
            null,
            false,
            null,
            noOverrideActor), CancellationToken.None);
        Assert.False(createForbidden.IsSuccess);
        Assert.Equal("forbidden", createForbidden.Error.Code);

        var latestDetail = (await h.Client.GetFromJsonAsync<AdminOrderDetailView>($"{OrdersRoot}/{salesCreated.Id}"))!;
        Assert.True(CatalogVersions.TryDecode(latestDetail.RowVersion, out var latestVersion));
        var patchForbidden = await patchHandler.Handle(new PatchAdminOrder(
            salesCreated.Id,
            latestVersion,
            HasLines: true,
            Lines: [new AdminOrderLineInput(variantId, 1, 450_000L, "Thử sửa giá không có quyền")],
            HasShipping: false,
            Shipping: null,
            HasDiscountVnd: false,
            DiscountVnd: null,
            HasDiscountNote: false,
            DiscountNote: null,
            HasShippingFeeVnd: false,
            ShippingFeeVnd: null,
            HasInternalNote: false,
            InternalNote: null,
            noOverrideActor), CancellationToken.None);
        Assert.False(patchForbidden.IsSuccess);
        Assert.Equal("forbidden", patchForbidden.Error.Code);
    }

    [Fact]
    public async Task BackfillOrderOwnerCreatesDeliveredWithZeroStockOrPaymentsAndOnlyDeliveredOutboxSalesForbidden403()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        var customerId = await CreateHttpCustomerAsync(h, "Khách Backfill", "0903111003");
        // Variant with on_hand = 0, available = 0 -> backfill MUST succeed without reserving stock!
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 0, priceVnd: 800_000L, avgCostVnd: 400_000L, suffix: "bf1");

        // 1. Sales attempting backfill -> 403 forbidden
        await h.LoginAsync(salesEmail, StaffNewPassword);
        using var salesBf = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = DefaultShipping(),
            isBackfilled = true,
            deliveredAt = DateTimeOffset.UtcNow.AddDays(-10)
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(salesBf, HttpStatusCode.Forbidden, "forbidden");

        // 2. Owner attempting backfill with future deliveredAt -> 422 invalid_delivered_at
        await h.LoginAsync();
        using var futureBf = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = DefaultShipping(),
            isBackfilled = true,
            deliveredAt = DateTimeOffset.UtcNow.AddHours(2)
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(futureBf, HttpStatusCode.UnprocessableEntity, "invalid_delivered_at");

        // 3. Owner attempting backfill without deliveredAt -> 400 validation_failed
        using var missingDateBf = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            shipping = DefaultShipping(),
            isBackfilled = true
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(missingDateBf, HttpStatusCode.BadRequest, "validation_failed");

        // 4. Owner attempting non-backfill WITH deliveredAt -> 400 validation_failed
        using var nonBfWithDate = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 1 } },
            shipping = DefaultShipping(),
            isBackfilled = false,
            deliveredAt = DateTimeOffset.UtcNow.AddDays(-1)
        }, Guid.CreateVersion7().ToString("D"));
        await AssertProblemAsync(nonBfWithDate, HttpStatusCode.BadRequest, "validation_failed");

        // 5. Owner creates valid backfill order -> 201 Created
        var deliveredAt = DateTimeOffset.UtcNow.AddDays(-14);
        using var okBf = await PostIdempotentJsonAsync(h.Client, OrdersRoot, new
        {
            customerId,
            salesChannel = "in_person",
            paymentMethod = "cod",
            lines = new[] { new { variantId, quantity = 2 } },
            shippingFeeVnd = 0L,
            shipping = DefaultShipping(),
            isBackfilled = true,
            deliveredAt
        }, Guid.CreateVersion7().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, okBf.StatusCode);
        var created = (await okBf.Content.ReadFromJsonAsync<AdminOrderCreatedView>())!;
        Assert.Equal(OrderStatus.Delivered, created.Status);
        Assert.Equal(OrderPaymentStatus.Unpaid, created.PaymentStatus);
        Assert.True(created.IsBackfilled);
        Assert.NotNull(created.DeliveredAt);

        // Assert DB invariants: 0 reservations, 0 stock_movements, 0 payments, on_hand/reserved unchanged (0/0)
        Assert.Equal(0L, await ScalarAsync<long>(h, "SELECT count(*) FROM inventory.reservations"));
        Assert.Equal(0L, await ScalarAsync<long>(h, $"SELECT count(*) FROM inventory.stock_movements WHERE variant_id = '{variantId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(h, $"SELECT count(*) FROM sales.payments WHERE order_id = '{created.Id}'"));
        Assert.Equal(0, await ScalarAsync<int>(h, $"SELECT on_hand FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));
        Assert.Equal(0, await ScalarAsync<int>(h, $"SELECT reserved FROM inventory.stock_levels WHERE variant_id = '{variantId}'"));

        // P2-F4: sales.orders create audit log records all 6 OnAdd columns and redacts all PII
        await using (var db = h.OwnerContext())
        {
            var createAudit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.EntityType == "sales.orders" && a.EntityId == created.Id && a.Action == "create");
            using var auditDoc = JsonDocument.Parse(createAudit.Changes);
            var root = auditDoc.RootElement;
            Assert.Equal(0L, root.GetProperty("discount_vnd").GetProperty("after").GetInt64());
            Assert.Equal(0L, root.GetProperty("shipping_fee_vnd").GetProperty("after").GetInt64());
            Assert.Equal(OrderPaymentStatus.Unpaid, root.GetProperty("payment_status").GetProperty("after").GetString());
            Assert.True(root.GetProperty("is_backfilled").GetProperty("after").GetBoolean());
            Assert.Equal(JsonValueKind.String, root.GetProperty("created_at").GetProperty("after").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("updated_at").GetProperty("after").ValueKind);
            Assert.Equal("[redacted]", root.GetProperty("contact_name").GetProperty("after").GetString());
            Assert.Equal("[redacted]", root.GetProperty("contact_phone").GetProperty("after").GetString());
            Assert.Equal("[redacted]", root.GetProperty("receiver_name").GetProperty("after").GetString());
            Assert.Equal("[redacted]", root.GetProperty("receiver_phone").GetProperty("after").GetString());
            Assert.Equal("[redacted]", root.GetProperty("address_line").GetProperty("after").GetString());
            Assert.DoesNotContain("Khách Backfill", createAudit.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain("0903111003", createAudit.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain("Nguyễn Người Nhận", createAudit.Changes, StringComparison.Ordinal);
        }

        // Assert outbox contains ONLY OrderDelivered(ReasonCode="backfilled"), 0 OrderPlaced, and processes cleanly
        await using (var db = h.OwnerContext())
        {
            var outboxRows = await db.OutboxMessages.AsNoTracking().ToArrayAsync();
            var deliveredMsg = Assert.Single(outboxRows);
            Assert.Equal(OrderEventTypes.OrderDelivered, deliveredMsg.Type);
            Assert.True(OrderOutboxMessage.TryParse(deliveredMsg.Payload, out var parsed));
            Assert.Equal(created.Id, parsed.OrderId);
            Assert.Equal(OrderDeliveredReasonCodes.Backfilled, parsed.ReasonCode);
        }

        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            var processed = await processor.ProcessAsync(10, CancellationToken.None);
            Assert.True(processed >= 1);
        }

        await using (var db = h.OwnerContext())
        {
            var msg = await db.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal("processed", msg.Status);
        }
    }

    [Fact]
    public async Task PermissionsCsrfAndHttpsMatrixEnforcedAcrossAllEightAdminOrdersEndpoints()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var contentEmail = await CreateStaffAsync(h, "content");
        var randomId = Guid.CreateVersion7();

        // 1. Anonymous -> 401 unauthenticated
        using var anonClient = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var anonList = await anonClient.GetAsync(OrdersRoot);
        await AssertProblemAsync(anonList, HttpStatusCode.Unauthorized, "unauthenticated");
        using var anonSlip = await anonClient.GetAsync($"{OrdersRoot}/{randomId}/packing-slip");
        await AssertProblemAsync(anonSlip, HttpStatusCode.Unauthorized, "unauthenticated");

        // 2. HTTP (non-HTTPS) -> 400 https_required
        using var httpClient = h.Factory.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        using var httpList = await httpClient.GetAsync(OrdersRoot);
        await AssertProblemAsync(httpList, HttpStatusCode.BadRequest, "https_required");

        // 3. Content role -> 403 forbidden on ALL 8 endpoints (including packing-slip)
        await h.LoginAsync(contentEmail, StaffNewPassword);
        var csrf = await GetCsrfTokenAsync(h.Client);

        foreach (var path in new[] { OrdersRoot, $"{OrdersRoot}/{randomId}", $"{OrdersRoot}/{randomId}/packing-slip" })
        {
            using var resp = await h.Client.GetAsync(path);
            await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
        }

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, OrdersRoot),
            (HttpMethod.Patch, $"{OrdersRoot}/{randomId}"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/confirm"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/cancel"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/reorder")
        })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-CSRF-TOKEN", csrf);
            using var resp = await h.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
        }

        // 4. Owner missing X-CSRF-TOKEN on POST/PATCH -> 400 csrf_invalid
        await h.LoginAsync();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, OrdersRoot),
            (HttpMethod.Patch, $"{OrdersRoot}/{randomId}"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/confirm"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/cancel"),
            (HttpMethod.Post, $"{OrdersRoot}/{randomId}/reorder")
        })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            using var resp = await h.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "csrf_invalid");
        }
    }

    internal static async Task SeedAdminLocationsAsync(P009Harness h)
    {
        var bundle = P009ImportTests.Bundle(1, AdminCsv);
        var plan = await h.PlanAsync(bundle);
        var result = await h.ApplyAsync(bundle, h.Approve(bundle, plan));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
    }

    internal static async Task<Guid> CreateHttpCustomerAsync(P009Harness h, string fullName, string phone)
    {
        using var resp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new { fullName, phone });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var view = (await resp.Content.ReadFromJsonAsync<TeaPremium.Application.Crm.CustomerDetailView>())!;
        return view.Id;
    }

    private static int _variantSeq = 8000;

    internal static async Task<Guid> SeedVariantOnlyAsync(
        P009Harness app, bool isUniquePiece, int onHand, long priceVnd, long? avgCostVnd, string suffix)
    {
        var productTypeId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var seq = Interlocked.Increment(ref _variantSeq);
        var sku = $"AM-PS-{seq:D4}";

        var movementSql = onHand > 0
            ? $"INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,ref_type,actor_type,occurred_at) VALUES ('{Guid.CreateVersion7()}','{variantId}','opening',{onHand},{(avgCostVnd ?? 0L)},{onHand},'import','system',now());"
            : "";

        await app.ExecuteAsync($"""
INSERT INTO catalog.product_types(id,code,name)
VALUES ('{productTypeId}','tea_p6_{suffix}_{seq}','Teapot P6') ON CONFLICT DO NOTHING;
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
VALUES ('{productId}','{productTypeId}','{sku}','pot-p6-{suffix}-{seq}','Ấm Tử Sa {suffix}','active',now(),200,'am tu sa {suffix}');
INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,is_unique_piece,low_stock_threshold)
VALUES ('{variantId}','{productId}','{sku}','active',{priceVnd},{(isUniquePiece ? "true" : "false")},1);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd)
VALUES ('{variantId}',{onHand},0,{(avgCostVnd.HasValue ? avgCostVnd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")});
{movementSql}
""");
        return variantId;
    }

    internal static object DefaultShipping() => new
    {
        receiverName = "Nguyễn Người Nhận",
        receiverPhone = "0909123456",
        provinceCode = "79",
        wardCode = "79001",
        addressLine = "10 Lê Lợi"
    };

    private static void AssertNoCostLeakage(string raw, long secretAvgCost)
    {
        Assert.DoesNotContain(secretAvgCost.ToString(System.Globalization.CultureInfo.InvariantCulture), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("317,500", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("317.500", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("avgCost", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Enqueue(formatter(state, exception));
            if (exception is not null) logs.Enqueue(exception.ToString());
        }
    }
}
