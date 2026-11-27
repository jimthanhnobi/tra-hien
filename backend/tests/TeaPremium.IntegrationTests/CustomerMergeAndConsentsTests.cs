using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using static TeaPremium.IntegrationTests.AdminOrdersPricingSecurityAndBackfillTests;
using static TeaPremium.IntegrationTests.CustomersApiTests;
using static TeaPremium.IntegrationTests.OrderConcurrencyTests;

namespace TeaPremium.IntegrationTests;

public sealed class CustomerMergeAndConsentsTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BrCrm09PutCustomerConsentUpsertsGrantAndRevokeWithStaffSourceAdvancesRowVersionAndFeedsHasConsentFilter()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        await h.LoginAsync(salesEmail, StaffNewPassword);

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Consent", phone = "0901112201" });

        // Grant consent via sales (customers.write) with optional rowVersion supplied.
        using var grantResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{customer.Id}/consents", new
        {
            channel = "zalo_personal",
            purpose = "marketing_new_products",
            status = "granted",
            evidence = "  Khách trả lời đồng ý qua Zalo 27/11/2026  ",
            rowVersion = customer.RowVersion
        });
        Assert.Equal(HttpStatusCode.OK, grantResp.StatusCode);
        var granted = (await grantResp.Content.ReadFromJsonAsync<CustomerConsentsResult>(WebJson))!;
        Assert.Equal(customer.Id, granted.CustomerId);
        Assert.NotEqual(customer.RowVersion, granted.RowVersion);
        Assert.Equal("granted", granted.Upserted.Status);
        Assert.Equal("staff", granted.Upserted.Source);
        Assert.Equal("Khách trả lời đồng ý qua Zalo 27/11/2026", granted.Upserted.Evidence);
        Assert.NotNull(granted.Upserted.CapturedBy);
        Assert.Single(granted.Consents);

        // Customer now appears in ?hasConsent=zalo_personal:marketing_new_products.
        var matching = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?hasConsent=zalo_personal:marketing_new_products"))!;
        Assert.Single(matching.Items);
        Assert.Equal(customer.Id, matching.Items[0].Id);

        // Revoke same (channel, purpose) without rowVersion (ASSUMPTION-P212-01 = A) -> updates in-place, zero duplicate row.
        using var revokeResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{customer.Id}/consents", new
        {
            channel = "zalo_personal",
            purpose = "marketing_new_products",
            status = "revoked",
            evidence = "Khách nhắn ngừng nhận tin khuyến mãi"
        });
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
        var revoked = (await revokeResp.Content.ReadFromJsonAsync<CustomerConsentsResult>(WebJson))!;
        Assert.NotEqual(granted.RowVersion, revoked.RowVersion);
        Assert.Single(revoked.Consents);
        Assert.Equal(granted.Upserted.Id, revoked.Upserted.Id);
        Assert.Equal("revoked", revoked.Upserted.Status);
        Assert.Equal("staff", revoked.Upserted.Source);

        // Customer is no longer returned by ?hasConsent=zalo_personal:marketing_new_products.
        var afterRevoke = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?hasConsent=zalo_personal:marketing_new_products"))!;
        Assert.Empty(afterRevoke.Items);
    }

    [Fact]
    public async Task BrCrm09PutCustomerConsentRejectsStaleRowVersionErasedOrMergedCustomerWith409()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var keeper = await CreateCustomerAsync(h, new { fullName = "Khách Giữ", phone = "0901112211" });
        var staleTarget = await CreateCustomerAsync(h, new { fullName = "Khách Stale", phone = "0901112212" });
        var erased = await CreateCustomerAsync(h, new { fullName = "Khách Xóa", phone = "0901112213" });
        var merged = await CreateCustomerAsync(h, new { fullName = "Khách Gộp", phone = "0901112214" });

        // Advance rowVersion on staleTarget.
        using var firstConsent = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{staleTarget.Id}/consents", new
        {
            channel = "email",
            purpose = "customer_care",
            status = "granted",
            evidence = "Đồng ý ban đầu"
        });
        Assert.Equal(HttpStatusCode.OK, firstConsent.StatusCode);

        using var staleResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{staleTarget.Id}/consents", new
        {
            channel = "email",
            purpose = "customer_care",
            status = "revoked",
            evidence = "Gửi kèm rowVersion cũ",
            rowVersion = staleTarget.RowVersion
        });
        await AssertProblemAsync(staleResp, HttpStatusCode.Conflict, "concurrency_conflict");

        await h.ExecuteAsync($"""
UPDATE crm.customers SET contact_status = 'erased', erased_at = now(), full_name = '[Đã xóa]', phone_e164 = NULL, zalo_name = NULL, search_text = '' WHERE id = '{erased.Id}';
UPDATE crm.customers SET merged_into_customer_id = '{keeper.Id}', phone_e164 = NULL WHERE id = '{merged.Id}';
""");

        using var erasedResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{erased.Id}/consents", new
        {
            channel = "sms",
            purpose = "customer_care",
            status = "granted",
            evidence = "Không được phép trên khách đã xóa"
        });
        await AssertProblemAsync(erasedResp, HttpStatusCode.Conflict, "customer_erased");

        using var mergedResp = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{merged.Id}/consents", new
        {
            channel = "sms",
            purpose = "customer_care",
            status = "granted",
            evidence = "Không được phép trên khách đã gộp"
        });
        var mergedProblem = await AssertProblemAsync(mergedResp, HttpStatusCode.Conflict, "customer_merged");
        Assert.Equal(keeper.Id, mergedProblem.GetProperty("data").GetProperty("mergedIntoCustomerId").GetGuid());
    }

    [Fact]
    public async Task BrCrm05O3bMergeCustomersMovesOrdersInteractionsDataRequestsVouchersAndRedemptionsRecomputesRealStatsAndSearchText()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var (_, productId, variantId, sku) = await SeedProductVariantAndCustomerAsync(h, isUniquePiece: false, onHand: 20, priceVnd: 1_000_000L, suffix: "m1");
        var customerA = await CreateCustomerAsync(h, new { fullName = "Nguyễn Văn Đích", phone = "0901222301" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Trần Thị Nguồn", phone = "0901222302", zaloName = "Zalo Nguồn" });

        var voucherBoundId = Guid.CreateVersion7();
        var voucherPublicId = Guid.CreateVersion7();
        var orderA1 = Guid.CreateVersion7(); // Delivered + overpayment refund (MUST NOT reduce LTV!) + partial commercial refund
        var orderB1 = Guid.CreateVersion7(); // Delivered + 100% returned via resolved refund + full commercial goods refund -> BR-RET-04 EXCLUDED!
        var orderB2 = Guid.CreateVersion7(); // Delivered backfilled -> INCLUDED!
        var orderB3 = Guid.CreateVersion7(); // Delivered + 100% replacement -> NOT excluded!
        var lineA1 = Guid.CreateVersion7();
        var lineB1 = Guid.CreateVersion7();
        var lineB3 = Guid.CreateVersion7();
        var retB1 = Guid.CreateVersion7();
        var retB3 = Guid.CreateVersion7();
        var interactionNoteB = Guid.CreateVersion7();
        var interactionCallB = Guid.CreateVersion7();
        var dataReqB = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO pricing.vouchers(id,code,kind,discount_type,discount_value,min_order_vnd,starts_at,ends_at,per_customer_limit,bound_customer_id,used_count,status) VALUES
 ('{voucherBoundId}','BOUND2026','referral_reward','fixed',100000,0,'2026-01-01T00:00:00Z','2027-01-01T00:00:00Z',1,'{customerB.Id}',0,'active'),
 ('{voucherPublicId}','PUBLIC2026','manual','fixed',50000,0,'2026-01-01T00:00:00Z','2027-01-01T00:00:00Z',5,NULL,2,'active');

INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,payment_status,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at,delivered_at,is_backfilled) VALUES
 ('{orderA1}','TP261101AA01','admin','zalo_personal','{customerA.Id}','delivered','bank_transfer','partially_refunded',
  'A','+84901222301','A','+84901222301','79','TP HCM','79001','P1','1 Le Loi',
  1000000,0,30000,1030000,'direct',true,'2026-11-01T02:00:00Z','2026-11-02T05:00:00Z',false),
 ('{orderB1}','TP261103BB01','admin','zalo_personal','{customerB.Id}','delivered','bank_transfer','refunded',
  'B','+84901222302','B','+84901222302','79','TP HCM','79001','P1','2 Le Loi',
  1000000,100000,30000,930000,'direct',true,'2026-11-03T02:00:00Z','2026-11-04T05:00:00Z',false),
 ('{orderB2}','TP261001BB02','admin','in_person','{customerB.Id}','delivered','cod','unpaid',
  'B','+84901222302','B','+84901222302','79','TP HCM','79001','P1','2 Le Loi',
  2000000,0,0,2000000,'legacy',false,'2026-10-01T02:00:00Z','2026-10-05T05:00:00Z',true),
 ('{orderB3}','TP261110BB03','admin','phone','{customerB.Id}','delivered','cod','paid',
  'B','+84901222302','B','+84901222302','79','TP HCM','79001','P1','2 Le Loi',
  500000,0,0,500000,'direct',false,'2026-11-10T02:00:00Z','2026-11-12T05:00:00Z',false);

INSERT INTO sales.order_lines(id,order_id,variant_id,product_id,sku,product_name,quantity,list_price_vnd,unit_price_vnd,price_source,line_total_vnd,returned_quantity) VALUES
 ('{lineA1}','{orderA1}','{variantId}','{productId}','{sku}','Am 1',1,1000000,1000000,'list',1000000,0),
 ('{lineB1}','{orderB1}','{variantId}','{productId}','{sku}','Am 1',1,1000000,1000000,'list',1000000,1),
 ('{lineB3}','{orderB3}','{variantId}','{productId}','{sku}','Am 1',1,500000,500000,'list',500000,1);

INSERT INTO sales.return_requests(id,code,order_id,reason,description,status,resolution,requested_at,resolved_at) VALUES
 ('{retB1}','DT-261105-001','{orderB1}','defective','Loi mem','resolved','refund','2026-11-05T00:00:00Z','2026-11-06T00:00:00Z'),
 ('{retB3}','DT-261113-002','{orderB3}','wrong_item','Doi mau','resolved','replacement','2026-11-13T00:00:00Z','2026-11-14T00:00:00Z');

INSERT INTO sales.return_lines(id,return_request_id,order_line_id,quantity,restockable) VALUES
 ('{Guid.CreateVersion7()}','{retB1}','{lineB1}',1,true),
 ('{Guid.CreateVersion7()}','{retB3}','{lineB3}',1,true);

INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by) VALUES
 ('{Guid.CreateVersion7()}','{orderA1}',NULL,'overpayment',200000,0,'bank_transfer','Hoan tien khach chuyen du','2026-11-02T06:00:00Z','{h.OwnerId}'),
 ('{Guid.CreateVersion7()}','{orderA1}',NULL,'adjustment',130000,0,'bank_transfer','Chiet khau thien chi','2026-11-02T07:00:00Z','{h.OwnerId}'),
 ('{Guid.CreateVersion7()}','{orderB1}','{retB1}','return',900000,0,'bank_transfer','Hoan du tien hang BR-RET-04','2026-11-06T01:00:00Z','{h.OwnerId}');

INSERT INTO pricing.voucher_redemptions(id,voucher_id,order_id,customer_id,amount_vnd,status) VALUES
 ('{Guid.CreateVersion7()}','{voucherPublicId}','{orderA1}','{customerA.Id}',50000,'applied'),
 ('{Guid.CreateVersion7()}','{voucherPublicId}','{orderB1}','{customerB.Id}',50000,'applied');

INSERT INTO crm.interactions(id,customer_id,staff_id,channel,kind,content,occurred_at) VALUES
 ('{interactionNoteB}','{customerB.Id}','{h.OwnerId}','other','note','Ghi chu moi hon nhung khong tinh last_contacted','2026-11-20T10:00:00Z'),
 ('{interactionCallB}','{customerB.Id}','{h.OwnerId}','phone','consultation','Tu van am tu sa','2026-11-18T09:00:00Z');

INSERT INTO crm.data_requests(id,customer_id,request_type,status,channel,requested_at) VALUES
 ('{dataReqB}','{customerB.Id}','export','completed','zalo_personal','2026-11-15T00:00:00Z');
""");

        using var mergeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id,
            rowVersion = customerA.RowVersion,
            mergeRowVersion = customerB.RowVersion
        });
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);
        var mergedA = (await mergeResp.Content.ReadFromJsonAsync<CustomerDetailView>(WebJson))!;

        // O3b Phương án 3 LTV check:
        // - orderA1: 1_030_000 - 130_000 (adjustment ONLY; 200_000 overpayment NOT subtracted!) = 900_000
        // - orderB1: EXCLUDED under BR-RET-04 (100% resolved refund return + 900_000 >= subtotal-discount)
        // - orderB2: backfilled 2_000_000 INCLUDED
        // - orderB3: replacement 500_000 INCLUDED (replacement never triggers BR-RET-04!)
        // Total: 3 orders, 3_400_000 VND, firstDeliveredAt = 2026-10-05T05:00:00Z, lastDeliveredAt = 2026-11-12T05:00:00Z
        Assert.Equal(3, mergedA.Stats.DeliveredOrdersCount);
        Assert.Equal(3_400_000L, mergedA.Stats.LifetimeValueVnd);
        Assert.Equal(new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc), mergedA.Stats.FirstDeliveredAt);
        Assert.Equal(new DateTime(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc), mergedA.Stats.LastDeliveredAt);
        // BR-CRM-07: consultation (2026-11-18), NOT note (2026-11-20).
        Assert.Equal(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc), mergedA.Stats.LastContactedAt);

        await using var db = h.OwnerContext();
        var stubB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
        Assert.Equal(customerA.Id, stubB.MergedIntoCustomerId);
        Assert.Null(stubB.PhoneE164);
        Assert.Equal(0, stubB.DeliveredOrdersCount);
        Assert.Equal(0L, stubB.LifetimeValueVnd);
        Assert.Null(stubB.FirstDeliveredAt);
        Assert.Null(stubB.LastDeliveredAt);
        Assert.Null(stubB.LastContactedAt);
        Assert.DoesNotContain("0901222302", stubB.SearchText, StringComparison.Ordinal);

        Assert.Equal(4, await db.Set<PersistenceOrder>().CountAsync(x => x.CustomerId == customerA.Id));
        Assert.Equal(0, await db.Set<PersistenceOrder>().CountAsync(x => x.CustomerId == customerB.Id));
        Assert.Equal(2, await db.Set<Interaction>().CountAsync(x => x.CustomerId == customerA.Id));
        Assert.Equal(1, await db.Set<DataRequest>().CountAsync(x => x.CustomerId == customerA.Id));
        Assert.Equal(customerA.Id, (await db.Set<Voucher>().SingleAsync(x => x.Id == voucherBoundId)).BoundCustomerId);
        Assert.Equal(2, await db.Set<VoucherRedemption>().CountAsync(x => x.CustomerId == customerA.Id));
    }

    [Fact]
    public async Task BrCrm05G17aMergeCustomersPhoneOnlySourceWithNullZaloClearsPhoneAndSatisfiesCkCustomerContactUnderTpApp()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách A", phone = "0901222311" });
        // Source B has phone ONLY (zalo_name IS NULL).
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B Chỉ SĐT", phone = "0901222312" });
        Assert.Null(customerB.ZaloName);

        using var mergeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id
        });
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);

        await using (var db = h.OwnerContext())
        {
            var stubB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
            Assert.Equal(customerA.Id, stubB.MergedIntoCustomerId);
            Assert.Null(stubB.PhoneE164);
            Assert.Null(stubB.ZaloName);
        }

        // Old phone of B is released so creating a brand-new customer with 0901222312 succeeds!
        var reused = await CreateCustomerAsync(h, new { fullName = "Khách Mới Dùng Lại SĐT", phone = "0901222312" });
        Assert.Equal("+84901222312", reused.PhoneE164);
    }

    [Fact]
    public async Task BrCrm05MergeCustomersResolvesCollisionsOnTagsDefaultAddressInterestsAndConsentsWithout23505()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách Va Chạm A", phone = "0901222321" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách Va Chạm B", phone = "0901222322" });

        var tagShared = Guid.CreateVersion7();
        var tagOnlyB = Guid.CreateVersion7();
        var addrA = Guid.CreateVersion7();
        var addrB = Guid.CreateVersion7();
        var materialShared = Guid.CreateVersion7();
        var materialOnlyB = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP Ho Chi Minh') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;

INSERT INTO crm.tags(id,name,color) VALUES
 ('{tagShared}','VIP Shared','#112233'),
 ('{tagOnlyB}','B Only Tag','#445566');
INSERT INTO crm.customer_tags(customer_id,tag_id) VALUES
 ('{customerA.Id}','{tagShared}'),
 ('{customerB.Id}','{tagShared}'),
 ('{customerB.Id}','{tagOnlyB}');

INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default) VALUES
 ('{addrA}','{customerA.Id}','Nguoi A','+84901222321','79','79001','1 Le Loi',true),
 ('{addrB}','{customerB.Id}','Nguoi B','+84901222322','79','79001','2 Le Loi',true);

INSERT INTO crm.customer_interests(id,customer_id,interest_type,ref_id,price_band,source,score) VALUES
 ('{Guid.CreateVersion7()}','{customerA.Id}','material','{materialShared}',NULL,'order',3),
 ('{Guid.CreateVersion7()}','{customerB.Id}','material','{materialShared}',NULL,'order',4),
 ('{Guid.CreateVersion7()}','{customerA.Id}','price_band',NULL,'3m_10m','order',2),
 ('{Guid.CreateVersion7()}','{customerB.Id}','price_band',NULL,'3m_10m','order',5),
 ('{Guid.CreateVersion7()}','{customerB.Id}','material','{materialOnlyB}',NULL,'manual',1);

INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at) VALUES
 ('{Guid.CreateVersion7()}','{customerA.Id}','zalo_personal','marketing_new_products','granted','staff','A cu hon','2026-11-10T00:00:00Z'),
 ('{Guid.CreateVersion7()}','{customerB.Id}','zalo_personal','marketing_new_products','revoked','staff','B moi hon','2026-11-20T00:00:00Z'),
 ('{Guid.CreateVersion7()}','{customerA.Id}','email','marketing_promotions','granted','staff','A moi hon','2026-11-25T00:00:00Z'),
 ('{Guid.CreateVersion7()}','{customerB.Id}','email','marketing_promotions','revoked','staff','B cu hon','2026-11-15T00:00:00Z'),
 ('{Guid.CreateVersion7()}','{customerB.Id}','sms','customer_care','granted','checkout','Chi B co','2026-11-18T00:00:00Z');
""");

        using var mergeResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id
        });
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);
        var merged = (await mergeResp.Content.ReadFromJsonAsync<CustomerDetailView>(WebJson))!;

        // Tags: union of shared + B-only (2 total).
        Assert.Equal(2, merged.Tags.Count);
        Assert.Contains(merged.Tags, t => t.Id == tagShared);
        Assert.Contains(merged.Tags, t => t.Id == tagOnlyB);

        // Addresses: both present, A's address remains default, B's address demoted to non-default.
        Assert.Equal(2, merged.Addresses.Count);
        Assert.True(merged.Addresses.Single(a => a.Id == addrA).IsDefault);
        Assert.False(merged.Addresses.Single(a => a.Id == addrB).IsDefault);

        // Interests: scores summed on collisions (3+4=7, 2+5=7) + unique kept.
        Assert.Equal(3, merged.Interests.Count);
        Assert.Equal(7, merged.Interests.Single(i => i.InterestType == "material" && i.RefId == materialShared).Score);
        Assert.Equal(7, merged.Interests.Single(i => i.InterestType == "price_band" && i.PriceBand == "3m_10m").Score);
        Assert.Equal(1, merged.Interests.Single(i => i.InterestType == "material" && i.RefId == materialOnlyB).Score);

        // Consents: newer captured_at wins on each (channel, purpose).
        Assert.Equal(3, merged.Consents.Count);
        Assert.Equal("revoked", merged.Consents.Single(c => c.Channel == "zalo_personal" && c.Purpose == "marketing_new_products").Status);
        Assert.Equal("granted", merged.Consents.Single(c => c.Channel == "email" && c.Purpose == "marketing_promotions").Status);
        Assert.Equal("granted", merged.Consents.Single(c => c.Channel == "sms" && c.Purpose == "customer_care").Status);
    }

    [Fact]
    public async Task G17bRp20105MergeCustomersFlattensChainCIntoBIntoAHandlesReferrerSelfRefAndRejectsSelfErasedOrAlreadyMerged()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách A", phone = "0901222331" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B", phone = "0901222332" });
        var customerC = await CreateCustomerAsync(h, new { fullName = "Khách C", phone = "0901222333" });
        var customerD = await CreateCustomerAsync(h, new { fullName = "Khách D ReferredByB", phone = "0901222334", referredByCustomerId = customerB.Id });
        var erased = await CreateCustomerAsync(h, new { fullName = "Khách Erased", phone = "0901222335" });

        // Point A.referred_by_customer_id = B and B.referred_by_customer_id = A (would violate ck_customer_not_self_ref if mishandled!).
        await h.ExecuteAsync($"""
UPDATE crm.customers SET referred_by_customer_id = '{customerB.Id}' WHERE id = '{customerA.Id}';
UPDATE crm.customers SET referred_by_customer_id = '{customerA.Id}' WHERE id = '{customerB.Id}';
UPDATE crm.customers SET contact_status = 'erased', erased_at = now(), full_name = '[Đã xóa]', phone_e164 = NULL, zalo_name = NULL, search_text = '' WHERE id = '{erased.Id}';
""");

        // Self-merge A -> A rejected with 400 validation_failed (R-P201-05).
        using var selfResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = customerA.Id });
        await AssertProblemAsync(selfResp, HttpStatusCode.BadRequest, "validation_failed");

        // Merge with erased rejected with 409 customer_erased.
        using var erasedResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = erased.Id });
        await AssertProblemAsync(erasedResp, HttpStatusCode.Conflict, "customer_erased");

        // Merge C -> B first, then B -> A.
        using var mergeCB = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerB.Id}/merge", new { mergeCustomerId = customerC.Id });
        Assert.Equal(HttpStatusCode.OK, mergeCB.StatusCode);

        // Attempting to merge already-merged stub C into A -> 409 customer_merged.
        using var mergeCA = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = customerC.Id });
        await AssertProblemAsync(mergeCA, HttpStatusCode.Conflict, "customer_merged");

        // Merge B -> A flattens C -> A, clears self-ref on A & B, and moves D.referred_by_customer_id -> A.
        using var mergeBA = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new { mergeCustomerId = customerB.Id });
        Assert.Equal(HttpStatusCode.OK, mergeBA.StatusCode);

        await using var db = h.OwnerContext();
        var rowA = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerA.Id);
        var rowB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
        var rowC = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerC.Id);
        var rowD = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerD.Id);

        Assert.Null(rowA.ReferredByCustomerId);
        Assert.Null(rowB.ReferredByCustomerId);
        Assert.Equal(customerA.Id, rowB.MergedIntoCustomerId);
        Assert.Equal(customerA.Id, rowC.MergedIntoCustomerId);
        Assert.Equal(customerA.Id, rowD.ReferredByCustomerId);
    }

    [Fact]
    public async Task G28BrCrm05MergeCustomersWhenBothHaveAccountsReturns409AccountExistsAndLeavesDatabaseUntouched()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Tài Khoản A", phone = "0901222341" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Tài Khoản B", phone = "0901222342" });

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,status,security_stamp) VALUES
 ('{Guid.CreateVersion7()}','{customerA.Id}','+84901222341','active','stamp-a'),
 ('{Guid.CreateVersion7()}','{customerB.Id}','+84901222342','active','stamp-b');
""");

        using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id
        });
        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "account_exists");

        await using var db = h.OwnerContext();
        var rowB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
        Assert.Null(rowB.MergedIntoCustomerId);
        Assert.Equal("+84901222342", rowB.PhoneE164);
    }

    [Fact]
    public async Task G28BrIam04MergeCustomersTransfersSourceAccountResetsHistoryUnlockRotatesSecurityStampAndClampsPreExistingAddresses()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách A Lịch Sử Cũ", phone = "0901222351" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B Có Tài Khoản", phone = "0901222352" });

        var accountBId = Guid.CreateVersion7();
        var addrAId = Guid.CreateVersion7();
        var addrBId = Guid.CreateVersion7();
        var accountCreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        var newerAddressCreatedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        await h.ExecuteAsync($"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP Ho Chi Minh') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,phone_verified_at,history_unlocked_at,status,security_stamp,created_at) VALUES
 ('{accountBId}','{customerB.Id}','+84901222352','2026-11-10T10:05:00Z','2026-11-10T10:06:00Z','active','initial-stamp-b','2026-11-10T10:00:00Z');
INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default,created_at,updated_at) VALUES
 ('{addrAId}','{customerA.Id}','Nguoi A','+84901222351','79','79001','10 Le Loi',true,'2026-11-20T10:00:00Z','2026-11-20T10:00:00Z'),
 ('{addrBId}','{customerB.Id}','Nguoi B','+84901222352','79','79001','20 Le Loi',false,'2026-11-20T10:00:00Z','2026-11-20T10:00:00Z');
""");

        using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = h.OwnerContext();
        var movedAccount = await db.Set<CustomerAccount>().AsNoTracking().SingleAsync(x => x.Id == accountBId);
        Assert.Equal(customerA.Id, movedAccount.CustomerId);
        Assert.Null(movedAccount.HistoryUnlockedAt);
        Assert.Null(movedAccount.PhoneVerifiedAt);
        Assert.NotEqual("initial-stamp-b", movedAccount.SecurityStamp);

        var clampedAddrA = await db.Set<CustomerAddress>().AsNoTracking().SingleAsync(x => x.Id == addrAId);
        var clampedAddrB = await db.Set<CustomerAddress>().AsNoTracking().SingleAsync(x => x.Id == addrBId);
        Assert.True(clampedAddrA.CreatedAt < accountCreatedAt, $"Expected {clampedAddrA.CreatedAt:O} < {accountCreatedAt:O}");
        Assert.True(clampedAddrB.CreatedAt < accountCreatedAt, $"Expected {clampedAddrB.CreatedAt:O} < {accountCreatedAt:O}");
        Assert.True(clampedAddrA.CreatedAt < newerAddressCreatedAt);
    }

    [Fact]
    public async Task G28BrIam04MergeCustomersWhenOnlyTargetHasUnverifiedAccountClampsTransferredAddressesFromSourceAndRotatesStamp()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customerA = await CreateCustomerAsync(h, new { fullName = "Khách A Chưa Mở Khóa", phone = "0901222361" });
        var customerB = await CreateCustomerAsync(h, new { fullName = "Khách B Địa Chỉ Mới", phone = "0901222362" });

        var accountAId = Guid.CreateVersion7();
        var addrBId = Guid.CreateVersion7();
        var accountCreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

        await h.ExecuteAsync($"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP Ho Chi Minh') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,phone_verified_at,history_unlocked_at,status,security_stamp,created_at) VALUES
 ('{accountAId}','{customerA.Id}','+84901222361',NULL,NULL,'active','initial-stamp-a','2026-11-10T10:00:00Z');
INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line,is_default,created_at,updated_at) VALUES
 ('{addrBId}','{customerB.Id}','Nguoi B','+84901222362','79','79001','99 Dong Khoi',false,'2026-11-22T10:00:00Z','2026-11-22T10:00:00Z');
""");

        using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge", new
        {
            mergeCustomerId = customerB.Id
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = h.OwnerContext();
        var accA = await db.Set<CustomerAccount>().AsNoTracking().SingleAsync(x => x.Id == accountAId);
        Assert.Null(accA.HistoryUnlockedAt);
        Assert.NotEqual("initial-stamp-a", accA.SecurityStamp);

        var movedAddrB = await db.Set<CustomerAddress>().AsNoTracking().SingleAsync(x => x.Id == addrBId);
        Assert.Equal(customerA.Id, movedAddrB.CustomerId);
        Assert.True(movedAddrB.CreatedAt < accountCreatedAt, $"Expected {movedAddrB.CreatedAt:O} < {accountCreatedAt:O}");
    }

    [Fact]
    public async Task BrCrm05ConcurrentBidirectionalMergeAbAndBaNeverDeadlocksExactlyOneSucceedsAndOtherReturns409()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // Run 4 rounds of simultaneous A<-B vs B<-A merges to stress lock hierarchy & advisory lock on real PG17.
        using var client1 = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var client2 = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(client1, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(client2, "owner@example.test", P009Harness.Password);
        var csrf1 = (await client1.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrf2 = (await client2.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        for (var round = 0; round < 4; round++)
        {
            var customerA = await CreateCustomerAsync(h, new { fullName = $"Race A {round}", phone = $"09013330{round}1" });
            var customerB = await CreateCustomerAsync(h, new { fullName = $"Race B {round}", phone = $"09013330{round}2" });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task1 = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrf1);
                using var resp = await client1.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var task2 = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerB.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerA.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrf2);
                using var resp = await client2.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var outcomes = await Task.WhenAll(task1, task2);

            Assert.Single(outcomes, o => o.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(outcomes, o => o.StatusCode == HttpStatusCode.Conflict);
            using var doc = JsonDocument.Parse(conflict.Item2);
            Assert.Equal("customer_merged", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ConcurrentMergeAgainstCreateAdminOrderAndCreateInteractionOnSourceNeverDeadlocksOrOrphansChildRows()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await SeedAdminLocationsAsync(h);
        await h.LoginAsync();
        var variantId = await SeedVariantOnlyAsync(h, isUniquePiece: false, onHand: 50, priceVnd: 500_000L, avgCostVnd: 200_000L, suffix: "racemrg");

        using var clientMerge = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientOrder = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientInteraction = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientMerge, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientOrder, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientInteraction, "owner@example.test", P009Harness.Password);
        var csrfMerge = (await clientMerge.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfOrder = (await clientOrder.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfInteraction = (await clientInteraction.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        for (var round = 0; round < 4; round++)
        {
            var customerA = await CreateCustomerAsync(h, new { fullName = $"Merge Target {round}", phone = $"09016660{round}1" });
            var customerB = await CreateCustomerAsync(h, new { fullName = $"Merge Source {round}", phone = $"09016660{round}2" });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mergeTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfMerge);
                using var resp = await clientMerge.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var orderTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/orders")
                {
                    Content = JsonContent.Create(new
                    {
                        customerId = customerB.Id,
                        salesChannel = "phone",
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
            var interactionTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerB.Id}/interactions")
                {
                    Content = JsonContent.Create(new
                    {
                        channel = "phone",
                        kind = "consultation",
                        content = $"Concurrent interaction on B {round}"
                    })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfInteraction);
                using var resp = await clientInteraction.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var mergeOutcome = await mergeTask;
            var orderOutcome = await orderTask;
            var interactionOutcome = await interactionTask;

            Assert.Equal(HttpStatusCode.OK, mergeOutcome.StatusCode);
            Assert.True(orderOutcome.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected order status {(int)orderOutcome.StatusCode}: {orderOutcome.Item2}");
            Assert.True(interactionOutcome.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected interaction status {(int)interactionOutcome.StatusCode}: {interactionOutcome.Item2}");

            await using var db = h.OwnerContext();
            var stubB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
            Assert.Equal(customerA.Id, stubB.MergedIntoCustomerId);

            // ZERO orphaned child rows left on merged stub B!
            Assert.Equal(0, await db.Set<PersistenceOrder>().CountAsync(x => x.CustomerId == customerB.Id));
            Assert.Equal(0, await db.Set<Interaction>().CountAsync(x => x.CustomerId == customerB.Id));

            if (orderOutcome.StatusCode == HttpStatusCode.Created)
            {
                Assert.Equal(1, await db.Set<PersistenceOrder>().CountAsync(x => x.CustomerId == customerA.Id));
            }
            else
            {
                using var doc = JsonDocument.Parse(orderOutcome.Item2);
                Assert.Equal("customer_merged", doc.RootElement.GetProperty("code").GetString());
            }

            if (interactionOutcome.StatusCode == HttpStatusCode.Created)
            {
                Assert.Equal(1, await db.Set<Interaction>().CountAsync(x => x.CustomerId == customerA.Id));
            }
            else
            {
                using var doc = JsonDocument.Parse(interactionOutcome.Item2);
                Assert.Equal("customer_merged", doc.RootElement.GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task F01MergeForNoKeyUpdateConcurrentlyWithPatchReferredByCustomerIdNeverDeadlocks40P01()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // Login authenticated clients ONCE outside the loop so we never trip auth rate limiting.
        using var clientMerge = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientPatchB = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientPatchR = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientMerge, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientPatchB, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientPatchR, "owner@example.test", P009Harness.Password);
        var csrfMerge = (await clientMerge.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfPatchB = (await clientPatchB.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfPatchR = (await clientPatchR.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        // Part A — Deterministic PG17 2-customer deadlock reproducer (A < B in UUID byte order):
        // Tx1 holds FOR NO KEY UPDATE on B -> Merge(A, B) acquires FOR NO KEY UPDATE on A and blocks on B ->
        // Tx1 executes UPDATE crm.customers SET referred_by_customer_id = A WHERE id = B (taking FK FOR KEY SHARE on A).
        // Under FOR UPDATE this deterministically deadlocked with 40P01; under FOR NO KEY UPDATE it completes cleanly!
        {
            var c1 = await CreateCustomerAsync(h, new { fullName = "Det F01 One", phone = "0901777001" });
            var c2 = await CreateCustomerAsync(h, new { fullName = "Det F01 Two", phone = "0901777002" });
            var (customerA, customerB) = string.CompareOrdinal(c1.Id.ToString("D"), c2.Id.ToString("D")) < 0 ? (c1, c2) : (c2, c1);

            await using var conn = new NpgsqlConnection(h.RuntimeConnection);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var blockerPid = await QueryScalarIntAsync(conn, tx, "SELECT pg_backend_pid()");
            await using (var lockB = new NpgsqlCommand($"SELECT id FROM crm.customers WHERE id = '{customerB.Id}' FOR NO KEY UPDATE", conn, tx))
            {
                await lockB.ExecuteScalarAsync();
            }

            var mergeTask = Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfMerge);
                using var resp = await clientMerge.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            // Wait until Merge holds A and is blocked waiting for Tx1's lock on B.
            try
            {
                await WaitUntilBlockedByPidAsync(h, blockerPid, mergeTask);

                // Now acquire FK FOR KEY SHARE on A while still holding B -> must NOT deadlock 40P01!
                await using (var updateRef = new NpgsqlCommand(
                    $"UPDATE crm.customers SET referred_by_customer_id = '{customerA.Id}', updated_at = now() WHERE id = '{customerB.Id}'", conn, tx))
                {
                    await updateRef.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                await mergeTask.WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }

            var mergeOutcome = await mergeTask;
            Assert.Equal(HttpStatusCode.OK, mergeOutcome.StatusCode);

            await using var db = h.OwnerContext();
            var stubB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
            Assert.Equal(customerA.Id, stubB.MergedIntoCustomerId);
            Assert.Null(stubB.ReferredByCustomerId);
        }

        // Part B — 6-round parallel HTTP Task.WhenAll stress test (2-customer A < B + 3-customer R referred by B).
        for (var round = 0; round < 6; round++)
        {
            var c1 = await CreateCustomerAsync(h, new { fullName = $"F01 Pair1 {round}", phone = $"09017771{round}1" });
            var c2 = await CreateCustomerAsync(h, new { fullName = $"F01 Pair2 {round}", phone = $"09017771{round}2" });
            var (customerA, customerB) = string.CompareOrdinal(c1.Id.ToString("D"), c2.Id.ToString("D")) < 0 ? (c1, c2) : (c2, c1);
            var customerR = await CreateCustomerAsync(h, new
            {
                fullName = $"F01 Referred {round}",
                phone = $"09017771{round}3",
                referredByCustomerId = customerB.Id
            });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mergeTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfMerge);
                using var resp = await clientMerge.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var patchBTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{CustomersRoot}/{customerB.Id}")
                {
                    Content = JsonContent.Create(new { rowVersion = customerB.RowVersion, referredByCustomerId = customerA.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfPatchB);
                using var resp = await clientPatchB.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var patchRTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{CustomersRoot}/{customerR.Id}")
                {
                    Content = JsonContent.Create(new { rowVersion = customerR.RowVersion, referredByCustomerId = customerA.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfPatchR);
                using var resp = await clientPatchR.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var mergeOutcome = await mergeTask;
            var patchBOutcome = await patchBTask;
            var patchROutcome = await patchRTask;

            Assert.Equal(HttpStatusCode.OK, mergeOutcome.StatusCode);
            Assert.True(patchBOutcome.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Round {round}: unexpected PATCH B status {(int)patchBOutcome.StatusCode}: {patchBOutcome.Item2}");
            Assert.True(patchROutcome.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Round {round}: unexpected PATCH R status {(int)patchROutcome.StatusCode}: {patchROutcome.Item2}");

            if (patchBOutcome.StatusCode == HttpStatusCode.Conflict)
            {
                using var doc = JsonDocument.Parse(patchBOutcome.Item2);
                var code = doc.RootElement.GetProperty("code").GetString();
                Assert.True(code is "customer_merged" or "concurrency_conflict", $"Unexpected conflict code: {code}");
            }

            if (patchROutcome.StatusCode == HttpStatusCode.Conflict)
            {
                using var doc = JsonDocument.Parse(patchROutcome.Item2);
                Assert.Equal("concurrency_conflict", doc.RootElement.GetProperty("code").GetString());
            }

            await using var db = h.OwnerContext();
            var rowA = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerA.Id);
            var rowB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
            var rowR = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerR.Id);

            Assert.Equal(customerA.Id, rowB.MergedIntoCustomerId);
            Assert.Null(rowB.ReferredByCustomerId);
            Assert.NotEqual(rowA.Id, rowA.ReferredByCustomerId);
            Assert.Equal(customerA.Id, rowR.ReferredByCustomerId);
        }
    }

    [Fact]
    public async Task F02MergeRevalidatesReferredByCustomerIdAfterLockingNeverOverwritesConcurrentReassignmentOrClear()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        using var clientMerge = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var clientPatchR = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(clientMerge, "owner@example.test", P009Harness.Password);
        await LoginClientAsync(clientPatchR, "owner@example.test", P009Harness.Password);
        var csrfMerge = (await clientMerge.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        var csrfPatchR = (await clientPatchR.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;

        // Part A — Deterministic PG17 interleaving proof for BOTH cases:
        // Case 0: concurrent Tx1 reassigns R.referred_by_customer_id from B -> C while Merge(A, B) waits on R's row lock.
        // Case 1: concurrent Tx1 clears R.referred_by_customer_id from B -> NULL while Merge(A, B) waits on R's row lock.
        for (var mode = 0; mode < 2; mode++)
        {
            var reassignToC = mode == 0;
            var customerA = await CreateCustomerAsync(h, new { fullName = $"F02 Det A {mode}", phone = $"09018880{mode}1" });
            var customerB = await CreateCustomerAsync(h, new { fullName = $"F02 Det B {mode}", phone = $"09018880{mode}2" });
            var customerC = await CreateCustomerAsync(h, new { fullName = $"F02 Det C {mode}", phone = $"09018880{mode}3" });
            var customerR = await CreateCustomerAsync(h, new
            {
                fullName = $"F02 Det R {mode}",
                phone = $"09018880{mode}4",
                referredByCustomerId = customerB.Id
            });

            await using var conn = new NpgsqlConnection(h.RuntimeConnection);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var blockerPid = await QueryScalarIntAsync(conn, tx, "SELECT pg_backend_pid()");
            // Lock R BEFORE updating it so Merge's AsNoTracking() referrer scan sees R.referred_by_customer_id == B,
            // then blocks trying to acquire FOR NO KEY UPDATE on R!
            await using (var lockR = new NpgsqlCommand($"SELECT id FROM crm.customers WHERE id = '{customerR.Id}' FOR NO KEY UPDATE", conn, tx))
            {
                await lockR.ExecuteScalarAsync();
            }

            var mergeTask = Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfMerge);
                using var resp = await clientMerge.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            // Wait deterministically until Merge is blocked waiting on Tx1's lock on R.
            try
            {
                await WaitUntilBlockedByPidAsync(h, blockerPid, mergeTask);

                var updateSql = reassignToC
                    ? $"UPDATE crm.customers SET referred_by_customer_id = '{customerC.Id}', updated_at = now() WHERE id = '{customerR.Id}'"
                    : $"UPDATE crm.customers SET referred_by_customer_id = NULL, updated_at = now() WHERE id = '{customerR.Id}'";
                await using (var updateR = new NpgsqlCommand(updateSql, conn, tx))
                {
                    await updateR.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                await mergeTask.WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }

            var mergeOutcome = await mergeTask;
            Assert.Equal(HttpStatusCode.OK, mergeOutcome.StatusCode);

            await using var db = h.OwnerContext();
            var stubB = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerB.Id);
            var rowR = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerR.Id);
            Assert.Equal(customerA.Id, stubB.MergedIntoCustomerId);

            // Merge MUST NOT overwrite R.referred_by_customer_id back to A!
            if (reassignToC)
            {
                Assert.Equal(customerC.Id, rowR.ReferredByCustomerId);
            }
            else
            {
                Assert.Null(rowR.ReferredByCustomerId);
            }
        }

        // Part B — 4-round parallel HTTP Task.WhenAll: Merge(A, B) vs PATCH R (B -> C).
        for (var round = 0; round < 4; round++)
        {
            var customerA = await CreateCustomerAsync(h, new { fullName = $"F02 Race A {round}", phone = $"09018881{round}1" });
            var customerB = await CreateCustomerAsync(h, new { fullName = $"F02 Race B {round}", phone = $"09018881{round}2" });
            var customerC = await CreateCustomerAsync(h, new { fullName = $"F02 Race C {round}", phone = $"09018881{round}3" });
            var customerR = await CreateCustomerAsync(h, new
            {
                fullName = $"F02 Race R {round}",
                phone = $"09018881{round}4",
                referredByCustomerId = customerB.Id
            });

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mergeTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{CustomersRoot}/{customerA.Id}/merge")
                {
                    Content = JsonContent.Create(new { mergeCustomerId = customerB.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfMerge);
                using var resp = await clientMerge.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });
            var patchRTask = Task.Run(async () =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{CustomersRoot}/{customerR.Id}")
                {
                    Content = JsonContent.Create(new { rowVersion = customerR.RowVersion, referredByCustomerId = customerC.Id })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrfPatchR);
                using var resp = await clientPatchR.SendAsync(req);
                return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
            });

            barrier.SetResult();
            var mergeOutcome = await mergeTask;
            var patchROutcome = await patchRTask;

            Assert.Equal(HttpStatusCode.OK, mergeOutcome.StatusCode);
            Assert.True(patchROutcome.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Round {round}: unexpected PATCH R status {(int)patchROutcome.StatusCode}: {patchROutcome.Item2}");

            await using var db = h.OwnerContext();
            var rowR = await db.Set<Customer>().AsNoTracking().SingleAsync(x => x.Id == customerR.Id);

            if (patchROutcome.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(customerC.Id, rowR.ReferredByCustomerId);
            }
            else
            {
                using var doc = JsonDocument.Parse(patchROutcome.Item2);
                Assert.Equal("concurrency_conflict", doc.RootElement.GetProperty("code").GetString());
                Assert.Equal(customerA.Id, rowR.ReferredByCustomerId);
            }
        }
    }

    private static async Task<int> QueryScalarIntAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilBlockedByPidAsync(
        P009Harness h,
        int blockerPid,
        Task<(HttpStatusCode StatusCode, string Body)> mergeTask)
    {
        // Query via a single reused RuntimeConnection (tp_app) — same role as both Tx1 and Merge so pg_stat_activity
        // and pg_blocking_pids(pid) are fully visible without needing superuser privileges or churning pooled connections.
        await using var observerConn = new NpgsqlConnection(h.RuntimeConnection);
        await observerConn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND @blockerPid = ANY(pg_blocking_pids(pid))",
            observerConn);
        cmd.Parameters.AddWithValue("blockerPid", blockerPid);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (mergeTask.IsCompleted)
            {
                var early = await mergeTask;
                Assert.Fail($"Concurrent merge finished unexpectedly before blocking on PID {blockerPid}: HTTP {(int)early.StatusCode} {early.Body}");
            }

            var blockedCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            if (blockedCount > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for concurrent merge transaction to block on PID {blockerPid}.");
    }
}
