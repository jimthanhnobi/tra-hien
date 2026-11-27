using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Crm;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Crm;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using static TeaPremium.IntegrationTests.AdminOrdersPricingSecurityAndBackfillTests;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;
using static TeaPremium.IntegrationTests.PaymentRecordingAndIdempotencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class CustomerEraseAndExportTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AcP210BrCrm11EraseCustomerWithDeliveredOrdersAnonymizesAllPiiDisablesAccountDeletesCartsAndPreservesExactRevenueTotals()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var (_, productId, variantId, sku) = await SeedProductVariantAndCustomerAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 1_500_000L, suffix: "e1");
        var tag = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Tag Erase" })).Content.ReadFromJsonAsync<CustomerTagView>())!;
        var customer = await CreateCustomerAsync(h, new
        {
            fullName = "Đặng Trần Khách Xóa",
            phone = "0901444001",
            email = "khach.xoa@example.test",
            zaloName = "Zalo Khách Xóa",
            noteSummary = "Ghi chú riêng tư sẽ bị xóa",
            tagIds = new[] { tag.Id }
        });

        var accountId = Guid.CreateVersion7();
        var cartId = Guid.CreateVersion7();
        var order1Id = Guid.CreateVersion7();
        var order2Id = Guid.CreateVersion7();
        var orderCancelledId = Guid.CreateVersion7();
        var priorRequestId = Guid.CreateVersion7();
        var interactionOpenFollowUpId = Guid.CreateVersion7();
        var interactionDoneFollowUpId = Guid.CreateVersion7();
        var interactionNoFollowUpId = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_addresses(id,customer_id,label,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','Nha rieng','Đặng Trần Khách Xóa','+84901444001','79','79001','99 Nguyễn Huệ',true);
INSERT INTO crm.customer_interests(id,customer_id,interest_type,ref_id,price_band,source,score) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','price_band',NULL,'1m_3m','order',3);
INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','zalo_personal','marketing_new_products','granted','staff','Khách đồng ý qua Zalo','2026-11-20T00:00:00Z');
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,password_hash,email,email_verified_at,phone_verified_at,history_unlocked_at,status,failed_login_count,lockout_end,security_stamp) VALUES
 ('{accountId}','{customer.Id}','+84901444001','HASHED_SECRET','khach.xoa@example.test','2026-11-20T00:00:00Z','2026-11-20T00:00:00Z','2026-11-20T00:00:00Z','active',2,'2026-11-28T00:00:00Z','old-stamp-erase');
INSERT INTO crm.customer_logins(provider,provider_key,account_id,email) VALUES
 ('google','google-sub-secret-001','{accountId}','khach.xoa@example.test');
INSERT INTO sales.carts(id,token_hash,customer_account_id,expires_at) VALUES
 ('{cartId}','cart-hash-erase-001','{accountId}','2026-12-27T00:00:00Z');
INSERT INTO sales.cart_items(cart_id,variant_id,quantity) VALUES
 ('{cartId}','{variantId}',1);
INSERT INTO crm.interactions(id,customer_id,staff_id,channel,kind,content,occurred_at,follow_up_at,follow_up_done_at,follow_up_result) VALUES
 ('{interactionOpenFollowUpId}','{customer.Id}','{h.OwnerId}','zalo_personal','consultation','Nội dung tư vấn mở 0901444001','2026-11-20T05:00:00Z','2026-11-28T05:00:00Z',NULL,NULL),
 ('{interactionDoneFollowUpId}','{customer.Id}','{h.OwnerId}','phone','follow_up','Nội dung đã chăm sóc 0901444001','2026-11-18T05:00:00Z','2026-11-19T05:00:00Z','2026-11-19T06:00:00Z','Đã gọi lại anh Khách Xóa 0901444001'),
 ('{interactionNoFollowUpId}','{customer.Id}','{h.OwnerId}','other','note','Ghi chú không có hẹn 0901444001','2026-11-17T05:00:00Z',NULL,NULL,NULL);
INSERT INTO crm.data_requests(id,customer_id,request_type,status,channel,note,requested_at) VALUES
 ('{priorRequestId}','{customer.Id}','export','open','email','Ghi chú cũ chứa PII 0901444001','2026-11-21T00:00:00Z');
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,customer_account_id,status,payment_method,payment_status,
 contact_name,contact_phone,contact_email,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,customer_note,placed_at,delivered_at,cancelled_at) VALUES
 ('{order1Id}','TP261120EA01','admin','zalo_personal','{customer.Id}','{accountId}','delivered','bank_transfer','paid',
  'Đặng Trần Khách Xóa','+84901444001','khach.xoa@example.test','Người Nhận 1','+84901444002','79','TP HCM','79001','Phường 1','99 Nguyễn Huệ',
  1500000,100000,30000,1430000,'zalo_personal',true,'Giao giờ hành chính','2026-11-20T01:00:00Z','2026-11-22T01:00:00Z',NULL),
 ('{order2Id}','TP261123EA02','admin','phone','{customer.Id}','{accountId}','delivered','cod','partially_refunded',
  'Đặng Trần Khách Xóa','+84901444001','khach.xoa@example.test','Người Nhận 2','+84901444003','79','TP HCM','79001','Phường 1','88 Đồng Khởi',
  3000000,0,0,3000000,'direct',false,'Gọi trước 30 phút','2026-11-23T01:00:00Z','2026-11-24T01:00:00Z',NULL),
 ('{orderCancelledId}','TP261125EA03','admin','phone','{customer.Id}','{accountId}','cancelled','cod','unpaid',
  'Đặng Trần Khách Xóa','+84901444001','khach.xoa@example.test','Người Nhận 3','+84901444004','79','TP HCM','79001','Phường 1','77 Lê Lợi',
  500000,0,30000,530000,'direct',false,'Đơn hủy','2026-11-25T01:00:00Z',NULL,'2026-11-25T02:00:00Z');
INSERT INTO sales.order_lines(id,order_id,variant_id,product_id,sku,product_name,quantity,list_price_vnd,unit_price_vnd,price_source,line_total_vnd,unit_cost_vnd) VALUES
 ('{Guid.CreateVersion7()}','{order1Id}','{variantId}','{productId}','{sku}','Ấm Tử Sa',1,1500000,1500000,'list',1500000,800000),
 ('{Guid.CreateVersion7()}','{order2Id}','{variantId}','{productId}','{sku}','Ấm Tử Sa',2,1500000,1500000,'list',3000000,800000);
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status,received_at,confirmed_at) VALUES
 ('{Guid.CreateVersion7()}','{order1Id}','bank_transfer','manual',1430000,'confirmed','2026-11-20T02:00:00Z','2026-11-20T02:00:00Z'),
 ('{Guid.CreateVersion7()}','{order2Id}','cod','manual',3000000,'confirmed','2026-11-24T05:00:00Z','2026-11-24T05:00:00Z');
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by) VALUES
 ('{Guid.CreateVersion7()}','{order2Id}',NULL,'adjustment',200000,0,'bank_transfer','Hoàn thiện chí','2026-11-24T08:00:00Z','{h.OwnerId}');
""");

        // Snapshot exact financial totals BEFORE erase (AC-P2-10).
        var beforeR1 = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(subtotal_vnd - discount_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var beforeShipping = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(shipping_fee_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var beforeTotal = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(total_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var beforeCommercialRefunds = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(amount_vnd), 0)::bigint FROM sales.refunds WHERE kind IN ('return','adjustment')");

        using var eraseResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = "Khách yêu cầu xóa toàn bộ dữ liệu cá nhân theo quyền chủ thể dữ liệu",
            requestChannel = "zalo_personal",
            rowVersion = customer.RowVersion
        });
        Assert.Equal(HttpStatusCode.OK, eraseResp.StatusCode);
        var erased = (await eraseResp.Content.ReadFromJsonAsync<CustomerEraseResult>(WebJson))!;
        Assert.Equal(customer.Id, erased.CustomerId);
        Assert.Equal(customer.Code, erased.Code);
        Assert.Equal("erased", erased.ContactStatus);
        Assert.Equal(3, erased.AnonymizedOrdersCount);
        Assert.Equal(0, erased.ScrubbedMergedStubsCount);
        Assert.NotEqual(customer.RowVersion, erased.RowVersion);

        // AC-P2-10: Revenue totals unchanged to the exact VND!
        var afterR1 = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(subtotal_vnd - discount_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var afterShipping = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(shipping_fee_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var afterTotal = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(total_vnd), 0)::bigint FROM sales.orders WHERE status = 'delivered'");
        var afterCommercialRefunds = await ScalarAsync<long>(h, "SELECT COALESCE(SUM(amount_vnd), 0)::bigint FROM sales.refunds WHERE kind IN ('return','adjustment')");

        Assert.Equal(beforeR1, afterR1);
        Assert.Equal(beforeShipping, afterShipping);
        Assert.Equal(beforeTotal, afterTotal);
        Assert.Equal(beforeCommercialRefunds, afterCommercialRefunds);

        await using var db = h.OwnerContext();
        var row = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.Equal("erased", row.ContactStatus);
        Assert.NotNull(row.ErasedAt);
        Assert.Equal(CustomerRules.AnonymizedText, row.FullName);
        Assert.Null(row.PhoneE164);
        Assert.Null(row.Email);
        Assert.Null(row.ZaloName);
        Assert.Null(row.NoteSummary);
        Assert.Equal("", row.SearchText);
        Assert.Null(row.LastContactedAt);

        Assert.Empty(await db.Set<CustomerAddress>().Where(x => x.CustomerId == customer.Id).ToArrayAsync());
        Assert.Empty(await db.Set<CustomerTag>().Where(x => x.CustomerId == customer.Id).ToArrayAsync());
        Assert.Empty(await db.Set<CustomerInterest>().Where(x => x.CustomerId == customer.Id).ToArrayAsync());
        Assert.Empty(await db.Set<Consent>().Where(x => x.CustomerId == customer.Id).ToArrayAsync());
        Assert.Empty(await db.Set<CustomerLogin>().Where(x => x.AccountId == accountId).ToArrayAsync());
        Assert.Empty(await db.Set<Cart>().Where(x => x.CustomerAccountId == accountId).ToArrayAsync());
        Assert.Empty(await db.Set<CartItem>().Where(x => x.CartId == cartId).ToArrayAsync());

        // R-P204-01: interactions scrubbed, follow_up_result cleared, open follow-up closed, non-follow-up keeps follow_up_done_at NULL!
        var interactions = await db.Set<Interaction>().AsNoTracking().Where(x => x.CustomerId == customer.Id).ToDictionaryAsync(x => x.Id);
        Assert.All(interactions.Values, i =>
        {
            Assert.Equal(CustomerRules.AnonymizedText, i.Content);
            Assert.Null(i.FollowUpResult);
        });
        Assert.NotNull(interactions[interactionOpenFollowUpId].FollowUpDoneAt);
        Assert.Equal(new DateTime(2026, 11, 19, 6, 0, 0, DateTimeKind.Utc), interactions[interactionDoneFollowUpId].FollowUpDoneAt);
        Assert.Null(interactions[interactionNoFollowUpId].FollowUpDoneAt);

        // Prior data request scrubbed & closed; new completed erase data request recorded.
        var requests = await db.Set<DataRequest>().AsNoTracking().Where(x => x.CustomerId == customer.Id).ToDictionaryAsync(x => x.Id);
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[priorRequestId].Note);
        Assert.Equal("completed", requests[priorRequestId].Status);
        Assert.Equal("erase", requests[erased.DataRequestId].RequestType);
        Assert.Equal("completed", requests[erased.DataRequestId].Status);
        Assert.Equal("zalo_personal", requests[erased.DataRequestId].Channel);
        Assert.Null(requests[erased.DataRequestId].Note);

        // Account disabled, credentials wiped, security_stamp rotated.
        var account = await db.Set<CustomerAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId);
        Assert.Equal("disabled", account.Status);
        Assert.Null(account.LoginPhone);
        Assert.Null(account.Email);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Null(account.PhoneVerifiedAt);
        Assert.Null(account.HistoryUnlockedAt);
        Assert.Null(account.PasswordHash);
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Null(account.LockoutEnd);
        Assert.NotEqual("old-stamp-erase", account.SecurityStamp);

        // Orders anonymized while preserving money & status & province/ward.
        var orders = await db.Set<PersistenceOrder>().AsNoTracking().Where(x => x.CustomerId == customer.Id).ToArrayAsync();
        Assert.Equal(3, orders.Length);
        Assert.All(orders, o =>
        {
            Assert.Equal(CustomerRules.AnonymizedText, o.ContactName);
            Assert.Equal(CustomerRules.AnonymizedText, o.ReceiverName);
            Assert.Equal(CustomerRules.AnonymizedOrderPhone, o.ContactPhone);
            Assert.Equal(CustomerRules.AnonymizedOrderPhone, o.ReceiverPhone);
            Assert.Null(o.ContactEmail);
            Assert.Equal(CustomerRules.AnonymizedText, o.AddressLine);
            Assert.Null(o.CustomerNote);
            Assert.Equal("79", o.ProvinceCode);
        });
    }

    [Fact]
    public async Task AssumptionP21203BrCrm11EraseCustomerAnonymizesMultiLevelMergedStubsChainDIntoCIntoBIntoALeavingZeroResidualPii()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // O6b = B guard: DataRetentionJob MUST NOT exist or run in P2-12.
        var retentionTypes = new[] { typeof(CustomerPrivacyAudit).Assembly, typeof(Program).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("DataRetention", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(retentionTypes);

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách Đích A", phone = "0901444011", email = "a@example.test" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Stub Tầng 1 B", phone = "0901444012", email = "b@example.test", zaloName = "Zalo B" });
        var customerC = await CreateCustomerAsync(h, new { fullName = "Stub Tầng 2 C", phone = "0901444013", email = "c@example.test", zaloName = "Zalo C", noteSummary = "Ghi chú PII C" });
        var customerD = await CreateCustomerAsync(h, new { fullName = "Stub Tầng 3 D", phone = "0901444014", email = "d@example.test", zaloName = "Zalo D", noteSummary = "Ghi chú PII D" });

        // Construct an explicit 3-hop chain D -> C -> B -> A directly in DB to prove iterative transitive closure at arbitrary depth!
        await h.ExecuteAsync($"""
UPDATE crm.customers SET merged_into_customer_id = '{customerA.Id}', phone_e164 = NULL WHERE id = '{customerB.Id}';
UPDATE crm.customers SET merged_into_customer_id = '{customerB.Id}', phone_e164 = NULL WHERE id = '{customerC.Id}';
UPDATE crm.customers SET merged_into_customer_id = '{customerC.Id}', phone_e164 = NULL WHERE id = '{customerD.Id}';
""");

        using var eraseResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/erase", new
        {
            reason = "Xóa hồ sơ đích kèm toàn bộ stub đa tầng 3 cấp",
            requestChannel = "phone"
        });
        Assert.Equal(HttpStatusCode.OK, eraseResp.StatusCode);
        var result = (await eraseResp.Content.ReadFromJsonAsync<CustomerEraseResult>(WebJson))!;
        Assert.Equal(3, result.ScrubbedMergedStubsCount);

        await using var db = h.OwnerContext();
        var rows = await db.Set<Customer>().AsNoTracking()
            .Where(x => x.Id == customerA.Id || x.Id == customerB.Id || x.Id == customerC.Id || x.Id == customerD.Id)
            .ToDictionaryAsync(x => x.Id);

        Assert.Equal(4, rows.Count);
        foreach (var row in rows.Values)
        {
            Assert.Equal("erased", row.ContactStatus);
            Assert.NotNull(row.ErasedAt);
            Assert.Equal(CustomerRules.AnonymizedText, row.FullName);
            Assert.Null(row.PhoneE164);
            Assert.Null(row.Email);
            Assert.Null(row.ZaloName);
            Assert.Null(row.NoteSummary);
            Assert.Equal("", row.SearchText);
        }

        // Parent linkage preserved for audit trail.
        Assert.Equal(customerA.Id, rows[customerB.Id].MergedIntoCustomerId);
        Assert.Equal(customerB.Id, rows[customerC.Id].MergedIntoCustomerId);
        Assert.Equal(customerC.Id, rows[customerD.Id].MergedIntoCustomerId);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("confirmed")]
    [InlineData("shipping")]
    public async Task G28BrCrm11EraseCustomerBlocksWhenPendingConfirmedOrShippingOrderExists(string activeStatus)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Đơn Đang Chạy", phone = "0901444021" });
        await h.ExecuteAsync($"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,payment_status,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at) VALUES
 ('{Guid.CreateVersion7()}','TP261126AC01','admin','phone','{customer.Id}','{activeStatus}','cod','unpaid',
  'Khách Đơn Đang Chạy','+84901444021','Khách','+84901444021','79','TP HCM',NULL,'P1','1 Le Loi',
  500000,0,0,500000,'direct',true,'2026-11-26T00:00:00Z');
""");

        using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = "Thử xóa khi còn đơn đang xử lý",
            requestChannel = "phone"
        });
        var problem = await AssertProblemAsync(resp, HttpStatusCode.Conflict, "customer_erase_blocked");
        var reasons = problem.GetProperty("data").GetProperty("blockingReasons").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("active_orders", reasons);

        // PII must remain untouched.
        var unchanged = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}"))!;
        Assert.Equal("active", unchanged.ContactStatus);
        Assert.Equal("Khách Đơn Đang Chạy", unchanged.FullName);
    }

    [Theory]
    [InlineData("return_requested", "open_return_requests")]
    [InlineData("return_approved", "open_return_requests")]
    [InlineData("cod_awaiting_settlement", "awaiting_cod_settlement")]
    public async Task G28BrCrm11EraseCustomerBlocksWhenOpenReturnRequestOrAwaitingSettlementPaymentExists(
        string blockerCase,
        string expectedReason)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Chặn Đổi Trả COD", phone = "0901444031" });
        var orderId = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,payment_status,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at,delivered_at) VALUES
 ('{orderId}','TP261126BL01','admin','phone','{customer.Id}','delivered','cod','unpaid',
  'Khách','+84901444031','Khách','+84901444031','79','TP HCM',NULL,'P1','1 Le Loi',
  500000,0,0,500000,'direct',true,'2026-11-25T00:00:00Z','2026-11-26T00:00:00Z');
""");

        if (blockerCase == "return_requested")
        {
            await h.ExecuteAsync($"""
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,requested_at) VALUES
 ('{Guid.CreateVersion7()}','DT-261126-001','{orderId}','defective','Nứt quai','requested','2026-11-26T05:00:00Z');
""");
        }
        else if (blockerCase == "return_approved")
        {
            await h.ExecuteAsync($"""
INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at) VALUES
 ('{Guid.CreateVersion7()}','DT-261126-002','{orderId}','defective','Nứt quai','approved','refund','2026-11-26T05:00:00Z');
""");
        }
        else
        {
            await h.ExecuteAsync($"""
INSERT INTO sales.payments(id,order_id,method,provider,amount_vnd,status) VALUES
 ('{Guid.CreateVersion7()}','{orderId}','cod','manual',500000,'awaiting_settlement');
""");
        }

        using var blockedResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = "Thử xóa khi còn đổi trả mở hoặc COD chưa đối soát",
            requestChannel = "zalo_personal"
        });
        var problem = await AssertProblemAsync(blockedResp, HttpStatusCode.Conflict, "customer_erase_blocked");
        var reasons = problem.GetProperty("data").GetProperty("blockingReasons").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(expectedReason, reasons);

        // Resolving/rejecting the return or confirming/voiding the payment unblocks erase!
        if (blockerCase.StartsWith("return_", StringComparison.Ordinal))
        {
            await h.ExecuteAsync($"UPDATE sales.return_requests SET status = 'rejected' WHERE order_id = '{orderId}';");
        }
        else
        {
            await h.ExecuteAsync($"UPDATE sales.payments SET status = 'confirmed', confirmed_at = now() WHERE order_id = '{orderId}';");
        }

        using var unblockedResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = "Xóa thành công sau khi đóng hết điều kiện chặn",
            requestChannel = "zalo_personal"
        });
        Assert.Equal(HttpStatusCode.OK, unblockedResp.StatusCode);
    }

    [Fact]
    public async Task G28BrCrm11EraseCustomerBlocksOnMergedStubWithTargetCustomerIdDataAndRejectsSecondEraseWith409()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách Đích", phone = "0901444041" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B Đã Gộp", phone = "0901444042" });

        using var mergeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = customerB.Id });
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);

        // Erasing merged stub B directly returns 409 customer_merged with data.mergedIntoCustomerId == A.Id.
        using var eraseStubResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerB.Id}/erase", new
        {
            reason = "Thử xóa trực tiếp trên stub đã gộp",
            requestChannel = "phone"
        });
        var mergedProblem = await AssertProblemAsync(eraseStubResp, HttpStatusCode.Conflict, "customer_merged");
        Assert.Equal(customerA.Id, mergedProblem.GetProperty("data").GetProperty("mergedIntoCustomerId").GetGuid());

        // Erasing A succeeds, then second erase on A returns 409 customer_erased.
        using var firstErase = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/erase", new
        {
            reason = "Xóa lần đầu hợp lệ",
            requestChannel = "phone"
        });
        Assert.Equal(HttpStatusCode.OK, firstErase.StatusCode);

        using var secondErase = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/erase", new
        {
            reason = "Thử xóa lần hai",
            requestChannel = "phone"
        });
        await AssertProblemAsync(secondErase, HttpStatusCode.Conflict, "customer_erased");
    }

    [Fact]
    public async Task BrCrm11EraseCustomerHandlesAllInteractionFollowUpStatesAndMultipleDisabledAccountsWithoutConstraintViolation()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var c1 = await CreateCustomerAsync(h, new { fullName = "Tài Khoản Xóa 1", phone = "0901444051" });
        var c2 = await CreateCustomerAsync(h, new { fullName = "Tài Khoản Xóa 2", phone = "0901444052" });

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,email,status,security_stamp) VALUES
 ('{Guid.CreateVersion7()}','{c1.Id}','+84901444051','c1@example.test','active','s1'),
 ('{Guid.CreateVersion7()}','{c2.Id}','+84901444052','c2@example.test','active','s2');
INSERT INTO crm.interactions(id,customer_id,staff_id,channel,kind,content,occurred_at,follow_up_at,follow_up_done_at,follow_up_result) VALUES
 ('{Guid.CreateVersion7()}','{c1.Id}','{h.OwnerId}','phone','note','No follow-up','2026-11-20T00:00:00Z',NULL,NULL,NULL),
 ('{Guid.CreateVersion7()}','{c1.Id}','{h.OwnerId}','phone','consultation','Open follow-up','2026-11-21T00:00:00Z','2026-11-29T00:00:00Z',NULL,NULL),
 ('{Guid.CreateVersion7()}','{c1.Id}','{h.OwnerId}','phone','follow_up','Done follow-up','2026-11-22T00:00:00Z','2026-11-23T00:00:00Z','2026-11-23T01:00:00Z','Secret result');
""");

        // Erasing two distinct customers who both have customer_accounts must leave two disabled rows with login_phone=NULL & email=NULL without 23505/23514!
        using var r1 = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c1.Id}/erase", new { reason = "Xóa tài khoản 1", requestChannel = "account" });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        using var r2 = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c2.Id}/erase", new { reason = "Xóa tài khoản 2", requestChannel = "account" });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        await using var db = h.OwnerContext();
        var disabledAccounts = await db.Set<CustomerAccount>().AsNoTracking()
            .Where(x => x.CustomerId == c1.Id || x.CustomerId == c2.Id)
            .ToArrayAsync();
        Assert.Equal(2, disabledAccounts.Length);
        Assert.All(disabledAccounts, a =>
        {
            Assert.Equal("disabled", a.Status);
            Assert.Null(a.LoginPhone);
            Assert.Null(a.Email);
        });
    }

    [Fact]
    public async Task BrCrm13ExportCustomerDataReturnsFullCustomerJsonWithNoStoreHeaderExcludesSecretsCostsInternalNotesAndWritesAccessAudit()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var (_, productId, variantId, sku) = await SeedProductVariantAndCustomerAsync(h, isUniquePiece: false, onHand: 10, priceVnd: 1_200_000L, suffix: "ex1");
        var customer = await CreateCustomerAsync(h, new
        {
            fullName = "Khách Xuất Dữ Liệu",
            phone = "0901444061",
            email = "export@example.test",
            zaloName = "Zalo Export"
        });

        var accountId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_addresses(id,customer_id,label,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','Nhà','Khách Xuất Dữ Liệu','+84901444061','79','79001','12 Đồng Khởi',true);
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,password_hash,email,status,security_stamp) VALUES
 ('{accountId}','{customer.Id}','+84901444061','SUPER_SECRET_PASSWORD_HASH','export@example.test','active','SUPER_SECRET_SECURITY_STAMP');
INSERT INTO crm.customer_logins(provider,provider_key,account_id,email) VALUES
 ('google','SUPER_SECRET_PROVIDER_KEY','{accountId}','export@example.test');
INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','zalo_personal','marketing_new_products','granted','staff','Khách đồng ý qua Zalo ngày 20/11','2026-11-20T00:00:00Z');
INSERT INTO crm.customer_interests(id,customer_id,interest_type,ref_id,price_band,source,score) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','price_band',NULL,'1m_3m','order',2);
INSERT INTO crm.interactions(id,customer_id,staff_id,channel,kind,content,occurred_at) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','{h.OwnerId}','zalo_personal','consultation','Tư vấn ấm tử sa','2026-11-21T00:00:00Z');
INSERT INTO crm.data_requests(id,customer_id,request_type,status,channel,note,requested_at) VALUES
 ('{Guid.CreateVersion7()}','{customer.Id}','correct','completed','phone','Đổi email','2026-11-22T00:00:00Z');
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,customer_account_id,status,payment_method,payment_status,
 contact_name,contact_phone,contact_email,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,customer_note,internal_note,placed_at,delivered_at) VALUES
 ('{orderId}','TP261122EX01','admin','zalo_personal','{customer.Id}','{accountId}','delivered','bank_transfer','paid',
  'Khách Xuất Dữ Liệu','+84901444061','export@example.test','Khách Xuất Dữ Liệu','+84901444061','79','TP HCM','79001','Phường 1','12 Đồng Khởi',
  1200000,0,0,1200000,'zalo_personal',true,'Bọc kỹ hộp quà','INTERNAL_NOTE_SECRET_DO_NOT_EXPORT','2026-11-22T01:00:00Z','2026-11-23T01:00:00Z');
INSERT INTO sales.order_lines(id,order_id,variant_id,product_id,sku,product_name,quantity,list_price_vnd,unit_price_vnd,price_source,price_override_reason,line_total_vnd,unit_cost_vnd) VALUES
 ('{Guid.CreateVersion7()}','{orderId}','{variantId}','{productId}','{sku}','Ấm Chu Nê',1,1200000,1200000,'override','OVERRIDE_REASON_SECRET_DO_NOT_EXPORT',1200000,654321);
""");

        using var exportResp = await h.Client.GetAsync($"{CustomersRoot}/{customer.Id}/export");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/json", exportResp.Content.Headers.ContentType?.MediaType);
        Assert.True(exportResp.Headers.CacheControl?.NoStore == true);

        var rawJson = await exportResp.Content.ReadAsStringAsync();

        // Strictly zero forbidden secret/cost/internal values or field names anywhere in JSON tree!
        Assert.DoesNotContain("SUPER_SECRET_PASSWORD_HASH", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_SECURITY_STAMP", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_PROVIDER_KEY", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("INTERNAL_NOTE_SECRET_DO_NOT_EXPORT", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERRIDE_REASON_SECRET_DO_NOT_EXPORT", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("654321", rawJson, StringComparison.Ordinal);

        using (var doc = JsonDocument.Parse(rawJson))
        {
            AssertNoForbiddenExportProperties(doc.RootElement);
        }

        var export = JsonSerializer.Deserialize<CustomerDataExportView>(rawJson, WebJson)!;
        Assert.Equal(customer.Id, export.Customer.Id);
        Assert.Single(export.Addresses);
        Assert.NotNull(export.Account);
        Assert.Single(export.Account!.ExternalLogins);
        Assert.Equal("google", export.Account.ExternalLogins[0].Provider);
        Assert.Single(export.Consents);
        Assert.Equal("Khách đồng ý qua Zalo ngày 20/11", export.Consents[0].Evidence);
        Assert.Single(export.Interests);
        Assert.Single(export.Interactions);
        Assert.Single(export.DataRequests);
        Assert.Single(export.Orders);
        Assert.Single(export.Orders[0].Lines);

        // ASSUMPTION-P212-02 = A: writes exactly 1 audit_logs(action='access') row and does NOT auto-insert crm.data_requests!
        await using var db = h.OwnerContext();
        var accessAudits = await db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "crm.customers" && x.EntityId == customer.Id && x.Action == "access")
            .ToArrayAsync();
        Assert.Single(accessAudits);
        Assert.Equal("staff", accessAudits[0].ActorType);
        Assert.Equal(h.OwnerId, accessAudits[0].ActorId);
        Assert.Equal(1, await db.Set<DataRequest>().CountAsync(x => x.CustomerId == customer.Id));

        // Missing customer -> 404 without writing any access audit row.
        var missingId = Guid.CreateVersion7();
        using var missingResp = await h.Client.GetAsync($"{CustomersRoot}/{missingId}/export");
        await AssertProblemAsync(missingResp, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.EntityId == missingId && x.Action == "access"));
    }

    [Fact]
    public async Task Nfr07PermissionsAndCsrfSalesCanPutConsentsButGets403OnMergeEraseExportContentGets403OnAll()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "RBAC A", phone = "0901444071" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "RBAC B", phone = "0901444072" });
        var salesEmail = await CreateStaffAsync(h, "sales");
        var contentEmail = await CreateStaffAsync(h, "content");

        var privacyEndpoints = new (HttpMethod Method, string Path, object? Body, bool SalesAllowed)[]
        {
            (HttpMethod.Put, $"{CustomersRoot}/{customerA.Id}/consents", new { channel = "email", purpose = "customer_care", status = "granted", evidence = "Đồng ý email" }, true),
            (HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = customerB.Id }, false),
            (HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/erase", new { reason = "Yêu cầu xóa", requestChannel = "phone" }, false),
            (HttpMethod.Get, $"{CustomersRoot}/{customerA.Id}/export", null, false)
        };

        // 1. Unauthenticated -> 401 on all 4 endpoints.
        using (var anon = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            foreach (var (method, path, body, _) in privacyEndpoints)
            {
                using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                using var resp = await anon.SendAsync(req);
                await AssertProblemAsync(resp, HttpStatusCode.Unauthorized, "unauthenticated");
            }
        }

        // 2. Sales -> 200 on PUT consents, 403 on merge/erase/export.
        using (var salesClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(salesClient, salesEmail, StaffNewPassword);
            var csrf = (await salesClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            foreach (var (method, path, body, salesAllowed) in privacyEndpoints)
            {
                using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                if (method != HttpMethod.Get) req.Headers.Add("X-CSRF-TOKEN", csrf);
                using var resp = await salesClient.SendAsync(req);
                if (salesAllowed)
                {
                    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                }
                else
                {
                    await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
                }
            }
        }

        // 3. Content -> 403 on all 4 endpoints (AC-P2-09).
        using (var contentClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(contentClient, contentEmail, StaffNewPassword);
            var csrf = (await contentClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            foreach (var (method, path, body, _) in privacyEndpoints)
            {
                using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                if (method != HttpMethod.Get) req.Headers.Add("X-CSRF-TOKEN", csrf);
                using var resp = await contentClient.SendAsync(req);
                await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
            }
        }

        // 4. Owner missing CSRF header on PUT consents / POST merge / POST erase -> 400 csrf_invalid.
        foreach (var (method, path, body, _) in privacyEndpoints.Where(e => e.Method != HttpMethod.Get))
        {
            using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var resp = await h.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "csrf_invalid");
        }
    }

    [Fact]
    public async Task Nfr04Nfr12AuditLogsAndProblemDetailsNeverLeakRawPiiConsentEvidenceOrEraseReason()
    {
        var capturedLogs = new ConcurrentQueue<string>();
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogs)));
        });
        await h.LoginAsync();

        const string secretName = "Nguyễn Bí Mật Riêng Tư";
        const string secretPhone = "0901444081";
        const string secretEvidence = "Bằng chứng bí mật anh Hiên 0901444081 đồng ý";
        const string secretEraseReason = "Lý do xóa bí mật anh Hiên 0901444081 gọi điện";

        var customer = await CreateCustomerAsync(h, new
        {
            fullName = secretName,
            phone = secretPhone,
            email = "bimat@example.test"
        });

        using (var consentResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{customer.Id}/consents", new
        {
            channel = "zalo_personal",
            purpose = "marketing_new_products",
            status = "granted",
            evidence = secretEvidence
        }))
            Assert.Equal(HttpStatusCode.OK, consentResp.StatusCode);

        using (var exportResp = await h.Client.GetAsync($"{CustomersRoot}/{customer.Id}/export"))
            Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);

        using (var eraseResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = secretEraseReason,
            requestChannel = "phone"
        }))
            Assert.Equal(HttpStatusCode.OK, eraseResp.StatusCode);

        // Trigger a validation ProblemDetails with secret strings to confirm zero echo in ProblemDetails/logs.
        using var badResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase", new
        {
            reason = secretEraseReason,
            requestChannel = "invalid_channel"
        });
        var badBody = await badResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

        await using var db = h.OwnerContext();
        var logs = await db.AuditLogs.AsNoTracking().ToArrayAsync();

        var sensitiveNeedles = new[] { secretName, secretPhone, "+84901444081", "bimat@example.test", secretEvidence, secretEraseReason };
        foreach (var log in logs)
        {
            foreach (var needle in sensitiveNeedles)
            {
                Assert.DoesNotContain(needle, log.Changes, StringComparison.OrdinalIgnoreCase);
            }
        }

        var combinedHttpAndStdout = badBody + "\n" + string.Join("\n", capturedLogs);
        foreach (var needle in sensitiveNeedles)
        {
            Assert.DoesNotContain(needle, combinedHttpAndStdout, StringComparison.OrdinalIgnoreCase);
        }

        // Verify encrypted erase reason decrypts cleanly with CustomerPrivacyAudit.EraseReasonProtectorPurpose.
        var eraseAudit = logs.Single(x =>
        {
            if (x.EntityType != "crm.customers" || x.EntityId != customer.Id || x.Action != "state_change")
            {
                return false;
            }

            using var doc = JsonDocument.Parse(x.Changes);
            return doc.RootElement.TryGetProperty("operation", out var op) && op.GetString() == "erase";
        });
        using var changesDoc = JsonDocument.Parse(eraseAudit.Changes);
        Assert.Equal("data_protection_v1", changesDoc.RootElement.GetProperty("reason_format").GetString());
        var protectedReason = changesDoc.RootElement.GetProperty("reason").GetString()!;

        await using var scope = h.Factory.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(CustomerPrivacyAudit.EraseReasonProtectorPurpose);
        Assert.Equal(secretEraseReason, protector.Unprotect(protectedReason));
    }

    [Fact]
    public async Task RealTpAppDatabaseRoleExecutesConsentMergeEraseExportWithout42501WhileDeleteOnCustomersAndOrdersRemainsBlocked()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var cA = await CreateCustomerAsync(h, new { fullName = "TpApp A", phone = "0901444091" });
        var cB = await CreateCustomerAsync(h, new { fullName = "TpApp B", phone = "0901444092" });
        var orderId = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,payment_status,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at,delivered_at) VALUES
 ('{orderId}','TP261126TP01','admin','phone','{cB.Id}','delivered','cod','paid',
  'TpApp B','+84901444092','TpApp B','+84901444092','79','TP HCM',NULL,'P1','1 Le Loi',
  400000,0,0,400000,'direct',true,'2026-11-25T00:00:00Z','2026-11-26T00:00:00Z');
""");

        // All 4 P2-12 endpoints run through P009Harness under the real tp_app database role!
        using (var consentResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{cA.Id}/consents", new
        {
            channel = "phone",
            purpose = "customer_care",
            status = "granted",
            evidence = "Đồng ý dưới quyền tp_app"
        }))
            Assert.Equal(HttpStatusCode.OK, consentResp.StatusCode);

        using (var mergeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{cA.Id}/merge", new { mergeCustomerId = cB.Id }))
            Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);

        using (var exportResp = await h.Client.GetAsync($"{CustomersRoot}/{cA.Id}/export"))
            Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);

        using (var eraseResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{cA.Id}/erase", new
        {
            reason = "Xóa dưới quyền tp_app",
            requestChannel = "phone"
        }))
            Assert.Equal(HttpStatusCode.OK, eraseResp.StatusCode);

        // Verify real tp_app role still gets 42501 insufficient_privilege on DELETE crm.customers and DELETE sales.orders!
        await using var conn = new NpgsqlConnection(h.RuntimeConnection);
        await conn.OpenAsync();

        await using (var delCustomer = new NpgsqlCommand($"DELETE FROM crm.customers WHERE id = '{cA.Id}'", conn))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => delCustomer.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var delOrder = new NpgsqlCommand($"DELETE FROM sales.orders WHERE id = '{orderId}'", conn))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => delOrder.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task ConcurrentEraseCustomerAndCreateAdminOrderNeverDeadlocksAndNeverLeavesBothErasedAndActiveOrder()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 50, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "raceord");

        using var clientErase = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientOrder = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientErase, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientOrder, "owner@example.test", P009Harness.Password);
        var csrfErase = (await clientErase.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfOrder = (await clientOrder.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        for (var round = 0; round < 6; round++)
        {
            var customer = await CreateCustomerAsync(h, new { fullName = $"Race Erase Order {round}", phone = $"09015550{round}1" });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var eraseTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase")
                {
                    Content = JsonContent.Create(new { reason = "Race erase vs order", requestChannel = "phone" })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfErase);
                using var resp = await clientErase.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var orderTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/orders")
                {
                    Content = JsonContent.Create(new
                    {
                        customerId = customer.Id,
                        salesChannel = "zalo_personal",
                        paymentMethod = "cod",
                        lines = new[] { new { variantId, quantity = 1 } },
                        shipping = DefaultShipping()
                    })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfOrder);
                req.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
                using var resp = await clientOrder.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var eraseOutcome = await eraseTask;
            var orderOutcome = await orderTask;

            Assert.True((int)eraseOutcome.StatusCode < 500, $"Erase failed with {(int)eraseOutcome.StatusCode}: {eraseOutcome.Item2}");
            Assert.True((int)orderOutcome.StatusCode < 500, $"Order failed with {(int)orderOutcome.StatusCode}: {orderOutcome.Item2}");

            await using var db = h.OwnerContext();
            var row = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            var activeOrders = await db.Set<PersistenceOrder>().AsNoTracking()
                .CountAsync(x => x.CustomerId == customer.Id && (x.Status == "pending" || x.Status == "confirmed" || x.Status == "shipping"));

            // Strict serializable invariant: NEVER both erased AND active order!
            Assert.False(row.ContactStatus == "erased" && activeOrders > 0,
                $"Serializable violation in round {round}: customer erased while {activeOrders} active order(s) exist.");

            if (eraseOutcome.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(HttpStatusCode.Conflict, orderOutcome.StatusCode);
                using var doc = JsonDocument.Parse(orderOutcome.Item2);
                Assert.Equal("customer_erased", doc.RootElement.GetProperty("code").GetString());
                Assert.Equal("erased", row.ContactStatus);
                Assert.Equal(0, activeOrders);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Created, orderOutcome.StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, eraseOutcome.StatusCode);
                using var doc = JsonDocument.Parse(eraseOutcome.Item2);
                Assert.Equal("customer_erase_blocked", doc.RootElement.GetProperty("code").GetString());
                Assert.Equal("active", row.ContactStatus);
                Assert.Equal(1, activeOrders);
            }
        }
    }

    [Fact]
    public async Task ConcurrentEraseCustomerAndReorderDeliveredOrderNeverDeadlocksAndMaintainsStrictSerializability()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 50, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "racereord");

        using var clientErase = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientReorder = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientErase, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientReorder, "owner@example.test", P009Harness.Password);
        var csrfErase = (await clientErase.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfReorder = (await clientReorder.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        for (var round = 0; round < 4; round++)
        {
            var customer = await CreateCustomerAsync(h, new { fullName = $"Race Erase Reorder {round}", phone = $"09015560{round}1" });

            // Create a delivered order via real P2-06 backfill endpoint so reorder has valid catalog lines & shipping address.
            using var createResp = await PostIdempotentJsonAsync(h.Client, "/api/v1/admin/orders", new
            {
                customerId = customer.Id,
                salesChannel = "phone",
                paymentMethod = "cod",
                lines = new[] { new { variantId, quantity = 1 } },
                shipping = DefaultShipping(),
                isBackfilled = true,
                deliveredAt = DateTimeOffset.UtcNow.AddDays(-2)
            }, Guid.CreateVersion7().ToString("D"));
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var deliveredOrder = (await createResp.Content.ReadFromJsonAsync<TeaPremium.Application.Orders.AdminOrderCreatedView>(WebJson))!;

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var eraseTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase")
                {
                    Content = JsonContent.Create(new { reason = "Race erase vs reorder", requestChannel = "phone" })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfErase);
                using var resp = await clientErase.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var reorderTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{deliveredOrder.Id}/reorder")
                {
                    Content = JsonContent.Create(new { })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfReorder);
                using var resp = await clientReorder.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var eraseOutcome = await eraseTask;
            var reorderOutcome = await reorderTask;

            Assert.True((int)eraseOutcome.StatusCode < 500, $"Erase failed with {(int)eraseOutcome.StatusCode}: {eraseOutcome.Item2}");
            Assert.True((int)reorderOutcome.StatusCode < 500, $"Reorder failed with {(int)reorderOutcome.StatusCode}: {reorderOutcome.Item2}");

            await using var db = h.OwnerContext();
            var row = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            var activeOrders = await db.Set<PersistenceOrder>().AsNoTracking()
                .CountAsync(x => x.CustomerId == customer.Id && (x.Status == "pending" || x.Status == "confirmed" || x.Status == "shipping"));

            Assert.False(row.ContactStatus == "erased" && activeOrders > 0);
            if (eraseOutcome.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(HttpStatusCode.Conflict, reorderOutcome.StatusCode);
                Assert.Equal(0, activeOrders);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Created, reorderOutcome.StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, eraseOutcome.StatusCode);
                Assert.Equal(1, activeOrders);
            }
        }
    }

    [Fact]
    public async Task ConcurrentEraseCustomerAgainstCreateInteractionAndPatchCustomerNeverDeadlocksAndLeavesZeroUnscrubbedPii()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        using var clientErase = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientInteraction = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientPatch = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientErase, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientInteraction, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientPatch, "owner@example.test", P009Harness.Password);
        var csrfErase = (await clientErase.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfInteraction = (await clientInteraction.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfPatch = (await clientPatch.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        for (var round = 0; round < 4; round++)
        {
            var customer = await CreateCustomerAsync(h, new { fullName = $"Race Erase Mutate {round}", phone = $"09015570{round}1" });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var eraseTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/erase")
                {
                    Content = JsonContent.Create(new { reason = "Race erase vs interaction/patch", requestChannel = "phone" })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfErase);
                using var resp = await clientErase.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var interactionTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/interactions")
                {
                    Content = JsonContent.Create(new
                    {
                        channel = "zalo_personal",
                        kind = "consultation",
                        content = $"Raw interaction PII 09015570{round}1",
                        followUpAt = DateTime.UtcNow.AddDays(1).ToString("O")
                    })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfInteraction);
                using var resp = await clientInteraction.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var patchTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}")
                {
                    Content = JsonContent.Create(new
                    {
                        rowVersion = customer.RowVersion,
                        fullName = $"Updated PII Name {round}",
                        noteSummary = $"Updated PII Note {round}"
                    })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfPatch);
                using var resp = await clientPatch.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var eraseOutcome = await eraseTask;
            var interactionOutcome = await interactionTask;
            var patchOutcome = await patchTask;

            Assert.Equal(HttpStatusCode.OK, eraseOutcome.StatusCode);
            Assert.True(interactionOutcome.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected interaction status {(int)interactionOutcome.StatusCode}: {interactionOutcome.Item2}");
            Assert.True(patchOutcome.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Unexpected patch status {(int)patchOutcome.StatusCode}: {patchOutcome.Item2}");

            await using var db = h.OwnerContext();
            var row = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal("erased", row.ContactStatus);
            Assert.Equal(CustomerRules.AnonymizedText, row.FullName);
            Assert.Null(row.NoteSummary);
            Assert.Null(row.PhoneE164);

            var interactions = await db.Set<Interaction>().AsNoTracking()
                .Where(x => x.CustomerId == customer.Id)
                .ToArrayAsync();

            if (interactionOutcome.StatusCode == HttpStatusCode.Created)
            {
                // Committed before erase -> MUST have been completely scrubbed by erase!
                var item = Assert.Single(interactions);
                Assert.Equal(CustomerRules.AnonymizedText, item.Content);
                Assert.Null(item.FollowUpResult);
                Assert.NotNull(item.FollowUpDoneAt);
            }
            else
            {
                // Ran after erase -> rejected with 409 customer_erased and 0 interactions persisted.
                using var doc = JsonDocument.Parse(interactionOutcome.Item2);
                Assert.Equal("customer_erased", doc.RootElement.GetProperty("code").GetString());
                Assert.Empty(interactions);
            }
        }
    }

    private static void AssertNoForbiddenExportProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                Assert.DoesNotContain("password", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("securityStamp", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("providerKey", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unitCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("avgCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("grossProfit", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("priceOverrideReason", prop.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("internalNote", prop.Name, StringComparison.OrdinalIgnoreCase);
                AssertNoForbiddenExportProperties(prop.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoForbiddenExportProperties(item);
            }
        }
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
