using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class CustomersApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    internal const string CustomersRoot = "/api/v1/admin/customers";
    internal const string TagsRoot = "/api/v1/admin/tags";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateAndGetCustomerAllocatesKhSequenceNormalizesPhoneAndPopulates360Defaults()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");
        await h.LoginAsync(salesEmail, StaffNewPassword);

        using var firstResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "  Đặng Trần Hiên  ",
            phone = "0901.234-567",
            email = "hien@example.test",
            zaloName = "Hiên Trà",
            customerGroup = "wholesale",
            isVip = true,
            acquisitionSource = "zalo_personal",
            noteSummary = "Khách chuộng ấm Tử Sa"
        });
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        Assert.NotNull(firstResp.Headers.Location);
        var first = (await firstResp.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("KH001000", first.Code);
        Assert.Equal("Đặng Trần Hiên", first.FullName);
        Assert.Equal("+84901234567", first.PhoneE164);
        Assert.Equal("admin", first.Origin);
        Assert.Equal("wholesale", first.CustomerGroup);
        Assert.True(first.IsVip);
        Assert.Equal("zalo_personal", first.AcquisitionSource);
        Assert.Equal("active", first.ContactStatus);
        Assert.Equal(0, first.Stats.DeliveredOrdersCount);
        Assert.Equal(0L, first.Stats.LifetimeValueVnd);
        Assert.False(first.AccountSummary.HasAccount);
        Assert.Empty(first.Tags);
        Assert.Empty(first.Addresses);
        Assert.Empty(first.Interests);
        Assert.Empty(first.Consents);
        Assert.False(string.IsNullOrWhiteSpace(first.RowVersion));

        using var secondResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Nguyễn Bình",
            phone = "84321234567"
        });
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var second = (await secondResp.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("KH001001", second.Code);
        Assert.Equal("+84321234567", second.PhoneE164);

        var loaded = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{first.Id}"))!;
        Assert.Equal(first.Id, loaded.Id);
        Assert.Equal(first.Code, loaded.Code);

        var orders = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerOrderSummaryView>>($"{CustomersRoot}/{first.Id}/orders"))!;
        Assert.Empty(orders.Items);
        Assert.Equal(0L, orders.TotalItems);
        Assert.Equal(1, orders.Page);
        Assert.Equal(20, orders.PageSize);
    }

    [Fact]
    public async Task CreateCustomerWithZaloOnlyAndNoPhoneSucceedsWhileMissingBothFails400()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        using var zaloOnly = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Khách Chỉ Zalo",
            zaloName = "Zalo Trà Hiên"
        });
        Assert.Equal(HttpStatusCode.Created, zaloOnly.StatusCode);
        var created = (await zaloOnly.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Null(created.PhoneE164);
        Assert.Equal("Zalo Trà Hiên", created.ZaloName);

        using var missingBoth = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Không Liên Hệ"
        });
        await AssertProblemAsync(missingBoth, HttpStatusCode.BadRequest, "contact_required");

        using var badPhone = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Máy Bàn",
            phone = "02438223344"
        });
        await AssertProblemAsync(badPhone, HttpStatusCode.BadRequest, "invalid_phone");
    }

    [Fact]
    public async Task BrCrm02CreateCustomerDuplicateActivePhoneReturns409WithExistingCustomerIdIncludingConcurrentRace()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // Sequential duplicate returns 409 customer_phone_exists with data.existingCustomerId
        using var firstResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Khách Gốc",
            phone = "0901234567"
        });
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var original = (await firstResp.Content.ReadFromJsonAsync<CustomerDetailView>())!;

        using var dupResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Khách Trùng",
            phone = "+84 901-234-567"
        });
        var dupJson = await AssertProblemAsync(dupResp, HttpStatusCode.Conflict, "customer_phone_exists");
        Assert.Equal(original.Id, dupJson.GetProperty("data").GetProperty("existingCustomerId").GetGuid());

        // Concurrent race: 8 parallel POSTs with the same phone -> 1x 201, 7x 409 with winner UUID, 0x 500
        var racePhone = "0987654321";
        var clients = new List<HttpClient>(8);
        var tokens = new List<string>(8);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var client = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(client, "owner@example.test", P009Harness.Password);
                var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
                clients.Add(client);
                tokens.Add(csrf);
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 8).Select(async idx =>
            {
                await barrier.Task;
                using var req = new HttpRequestMessage(HttpMethod.Post, CustomersRoot)
                {
                    Content = JsonContent.Create(new { fullName = $"Race Customer {idx}", phone = racePhone })
                };
                req.Headers.Add("X-CSRF-TOKEN", tokens[idx]);
                using var resp = await clients[idx].SendAsync(req);
                var text = await resp.Content.ReadAsStringAsync();
                return (resp.StatusCode, text);
            }).ToArray();

            barrier.SetResult();
            var outcomes = await Task.WhenAll(tasks);

            var createdList = outcomes.Where(o => o.StatusCode == HttpStatusCode.Created).ToArray();
            var conflictList = outcomes.Where(o => o.StatusCode == HttpStatusCode.Conflict).ToArray();
            Assert.Single(createdList);
            Assert.Equal(7, conflictList.Length);

            var winnerId = JsonDocument.Parse(createdList[0].text).RootElement.GetProperty("id").GetGuid();
            foreach (var (_, body) in conflictList)
            {
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("customer_phone_exists", doc.RootElement.GetProperty("code").GetString());
                Assert.Equal(winnerId, doc.RootElement.GetProperty("data").GetProperty("existingCustomerId").GetGuid());
            }
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }

        // Merged customer does not block creating a new customer with the same phone
        var targetId = Guid.CreateVersion7();
        await h.ExecuteAsync($"INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('{targetId}','KH009999','Target','+84909999999','admin')");
        await h.ExecuteAsync($"UPDATE crm.customers SET merged_into_customer_id='{targetId}' WHERE id='{original.Id}'");

        using var afterMerge = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Khách Mới Sau Gộp",
            phone = "0901234567"
        });
        Assert.Equal(HttpStatusCode.Created, afterMerge.StatusCode);
    }

    [Fact]
    public async Task BrCrm14ListCustomersVietnameseUnaccentPhoneTailAndEscapedMetacharactersWorkWithGinIndex()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var target = await CreateCustomerAsync(h, new
        {
            fullName = "Đặng Trần Hiên",
            zaloName = "Trà Cụ Tử Sa",
            phone = "0901234567",
            email = "hien.tra@example.test"
        });
        await CreateCustomerAsync(h, new
        {
            fullName = "Lê Minh Quân",
            zaloName = "Quân 100% Thật",
            phone = "0912345678"
        });

        foreach (var query in new[] { "dang tran", "ĐẶNG", "tu sa", target.Code, "KH001", "0901 234 567", "+84901-234-567", "4567" })
        {
            var page = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
                $"{CustomersRoot}?q={Uri.EscapeDataString(query)}"))!;
            Assert.Contains(page.Items, x => x.Id == target.Id);
        }

        // LIKE metacharacters must be escaped literally
        var percentPage = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?q={Uri.EscapeDataString("100%")}"))!;
        Assert.Single(percentPage.Items);
        Assert.Equal("Lê Minh Quân", percentPage.Items[0].FullName);

        var underscorePage = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?q={Uri.EscapeDataString("a_b")}"))!;
        Assert.Empty(underscorePage.Items);
    }

    [Fact]
    public async Task ListCustomersSupportsGroupTagInterestConsentLtvContactStatusAndVietnamDateFilters()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        using var tagResp = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Khách Sưu Tầm", color = "#2A5C45" });
        Assert.Equal(HttpStatusCode.Created, tagResp.StatusCode);
        var tag = (await tagResp.Content.ReadFromJsonAsync<CustomerTagView>())!;

        var c1 = await CreateCustomerAsync(h, new
        {
            fullName = "An Nguyễn",
            phone = "0901111111",
            customerGroup = "wholesale",
            contactStatus = "active",
            tagIds = new[] { tag.Id }
        });
        var c2 = await CreateCustomerAsync(h, new
        {
            fullName = "Bình Trần",
            phone = "0902222222",
            customerGroup = "retail",
            contactStatus = "do_not_contact"
        });
        var merged = await CreateCustomerAsync(h, new
        {
            fullName = "Đã Gộp",
            zaloName = "Merged Zalo"
        });

        var materialId = Guid.CreateVersion7();
        // 2026-11-20 01:00 Vietnam time = 2026-11-19 18:00 UTC
        await h.ExecuteAsync($"""
UPDATE crm.customers SET lifetime_value_vnd = 5000000, delivered_orders_count = 3, last_delivered_at = '2026-11-19T18:00:00Z' WHERE id = '{c1.Id}';
UPDATE crm.customers SET lifetime_value_vnd = 500000, delivered_orders_count = 1, last_delivered_at = '2026-11-10T10:00:00Z' WHERE id = '{c2.Id}';
UPDATE crm.customers SET merged_into_customer_id = '{c1.Id}' WHERE id = '{merged.Id}';
INSERT INTO crm.customer_interests(id,customer_id,interest_type,ref_id,price_band,source,score) VALUES
 ('{Guid.CreateVersion7()}','{c1.Id}','material','{materialId}',NULL,'manual',5),
 ('{Guid.CreateVersion7()}','{c1.Id}','price_band',NULL,'3m_10m','order',2);
INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at) VALUES
 ('{Guid.CreateVersion7()}','{c1.Id}','zalo_personal','marketing_new_products','granted','staff','Khách đồng ý qua Zalo','2026-11-20T00:00:00Z');
""");

        // Merged customer excluded by default
        var all = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>($"{CustomersRoot}?sort=fullName:asc"))!;
        Assert.Equal(2L, all.TotalItems);
        Assert.Equal([c1.Id, c2.Id], all.Items.Select(x => x.Id).ToArray());

        // Combined filters
        var filtered = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?group=wholesale&tag={tag.Id}&interest=material:{materialId}&hasConsent=zalo_personal:marketing_new_products&minLtv=1000000&contactStatus=active&lastDeliveredAfter=2026-11-20&lastDeliveredBefore=2026-11-21"))!;
        Assert.Single(filtered.Items);
        Assert.Equal(c1.Id, filtered.Items[0].Id);
        Assert.Single(filtered.Items[0].Tags);

        // Price band interest filter
        var priceBand = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?interest=price_band:3m_10m"))!;
        Assert.Single(priceBand.Items);
        Assert.Equal(c1.Id, priceBand.Items[0].Id);

        // Sorting & pagination
        var sortedLtv = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>(
            $"{CustomersRoot}?sort=lifetimeValueVnd:desc&page=1&pageSize=1"))!;
        Assert.Equal(2L, sortedLtv.TotalItems);
        Assert.Single(sortedLtv.Items);
        Assert.Equal(c1.Id, sortedLtv.Items[0].Id);
    }

    [Fact]
    public async Task BrCrm04PatchCustomerUpdatesAllowedFieldsRebuildsSearchTextAndRejectsIdentityFields()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var created = await CreateCustomerAsync(h, new
        {
            fullName = "Nguyễn Cũ",
            phone = "0903333333"
        });

        // Reject every forbidden identity field on ordinary PATCH
        foreach (var forbiddenBody in new object[]
        {
            new { rowVersion = created.RowVersion, phone = "0904444444" },
            new { rowVersion = created.RowVersion, phoneE164 = "+84904444444" },
            new { rowVersion = created.RowVersion, origin = "web" },
            new { rowVersion = created.RowVersion, acquisitionSource = "facebook" },
            new { rowVersion = created.RowVersion, hadLegacyPurchase = true }
        })
        {
            using var resp = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{created.Id}", forbiddenBody);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "validation_failed");
        }

        // Valid partial PATCH updates allowed fields and rebuilds search_text
        using var patchResp = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{created.Id}", new
        {
            rowVersion = created.RowVersion,
            fullName = "Phạm Đổi Tên",
            zaloName = "Zalo Mới",
            email = "moi@example.test",
            customerGroup = "reseller",
            isVip = true,
            contactStatus = "do_not_contact",
            noteSummary = "Ưu tiên gọi sáng"
        });
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var updated = (await patchResp.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("Phạm Đổi Tên", updated.FullName);
        Assert.Equal("Zalo Mới", updated.ZaloName);
        Assert.Equal("moi@example.test", updated.Email);
        Assert.Equal("reseller", updated.CustomerGroup);
        Assert.True(updated.IsVip);
        Assert.Equal("do_not_contact", updated.ContactStatus);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);

        var found = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>($"{CustomersRoot}?q=pham%20doi%20ten"))!;
        Assert.Contains(found.Items, x => x.Id == created.Id);
    }

    [Fact]
    public async Task CustomerPatchAndCorrectIdentityStaleOrMalformedRowVersionReturn409And400()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var created = await CreateCustomerAsync(h, new
        {
            fullName = "Khách Concurrency",
            phone = "0905555555"
        });

        using var badRv = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{created.Id}", new
        {
            rowVersion = "not-base64",
            fullName = "Malformed"
        });
        await AssertProblemAsync(badRv, HttpStatusCode.BadRequest, "validation_failed");

        using var firstEdit = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{created.Id}", new
        {
            rowVersion = created.RowVersion,
            fullName = "Người Sửa Thứ Nhất"
        });
        Assert.Equal(HttpStatusCode.OK, firstEdit.StatusCode);

        using var stalePatch = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{created.Id}", new
        {
            rowVersion = created.RowVersion,
            fullName = "Người Sửa Thứ Hai"
        });
        await AssertProblemAsync(stalePatch, HttpStatusCode.Conflict, "concurrency_conflict");

        using var staleCorrect = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{created.Id}/correct-identity", new
        {
            rowVersion = created.RowVersion,
            phone = "0906666666",
            reason = "Sửa SĐT bằng rowVersion cũ"
        });
        await AssertProblemAsync(staleCorrect, HttpStatusCode.Conflict, "concurrency_conflict");
    }

    [Fact]
    public async Task BrCrm04CorrectIdentitySalesCanChangePhoneOnlyOwnerCanChangeOriginAcquisitionAndLegacyFlag()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        var salesEmail = await CreateStaffAsync(h, "sales");

        var c1 = await CreateCustomerAsync(h, new { fullName = "Khách 1", phone = "0907777771" });
        var c2 = await CreateCustomerAsync(h, new { fullName = "Khách 2", phone = "0907777772" });

        // Switch to sales
        await h.LoginAsync(salesEmail, StaffNewPassword);

        // Sales CANNOT change origin / acquisitionSource / hadLegacyPurchase
        foreach (var ownerOnlyPayload in new object[]
        {
            new { rowVersion = c1.RowVersion, origin = "web", reason = "Thử đổi origin" },
            new { rowVersion = c1.RowVersion, acquisitionSource = "facebook", reason = "Thử đổi nguồn" },
            new { rowVersion = c1.RowVersion, hadLegacyPurchase = true, reason = "Thử đổi legacy" }
        })
        {
            using var denied = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c1.Id}/correct-identity", ownerOnlyPayload);
            await AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        }

        // Sales CAN change phone
        using var phoneOk = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c1.Id}/correct-identity", new
        {
            rowVersion = c1.RowVersion,
            phone = "0907777779",
            reason = "Khách báo nhập sai số cuối"
        });
        Assert.Equal(HttpStatusCode.OK, phoneOk.StatusCode);
        var afterSales = (await phoneOk.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("+84907777779", afterSales.PhoneE164);

        // Duplicate phone on correct-identity -> 409 customer_phone_exists with existingCustomerId
        using var dupPhone = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c1.Id}/correct-identity", new
        {
            rowVersion = afterSales.RowVersion,
            phone = "0907777772",
            reason = "Đổi trùng số khách 2"
        });
        var dupJson = await AssertProblemAsync(dupPhone, HttpStatusCode.Conflict, "customer_phone_exists");
        Assert.Equal(c2.Id, dupJson.GetProperty("data").GetProperty("existingCustomerId").GetGuid());

        // Switch back to owner: CAN change all 4 fields
        await h.LoginAsync();
        using var ownerOk = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c1.Id}/correct-identity", new
        {
            rowVersion = afterSales.RowVersion,
            phone = "0907777788",
            origin = "legacy_import",
            acquisitionSource = "legacy",
            hadLegacyPurchase = true,
            reason = "Xác nhận khách cũ từ sổ cửa hàng"
        });
        Assert.Equal(HttpStatusCode.OK, ownerOk.StatusCode);
        var afterOwner = (await ownerOk.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("+84907777788", afterOwner.PhoneE164);
        Assert.Equal("legacy_import", afterOwner.Origin);
        Assert.Equal("legacy", afterOwner.AcquisitionSource);
        Assert.True(afterOwner.Stats.HadLegacyPurchase);
    }

    [Fact]
    public async Task CorrectIdentityWhenCustomerAccountExistsBlocksPhoneChangeOrClearWith409AccountExists()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new
        {
            fullName = "Khách Có Tài Khoản",
            phone = "0908888881",
            zaloName = "Zalo Dự Phòng"
        });

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,password_hash,email,phone_verified_at,history_unlocked_at,status,security_stamp)
VALUES ('{Guid.CreateVersion7()}','{customer.Id}','+84908888881','fake-hash','acc@example.test',now(),now(),'active','stamp-v1');
""");

        // GAP-P202-01 = C: changing phone when crm.customer_accounts row exists -> 409 account_exists
        using var changePhone = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/correct-identity", new
        {
            rowVersion = customer.RowVersion,
            phone = "0908888882",
            reason = "Thử đổi SĐT khi đã có tài khoản"
        });
        await AssertProblemAsync(changePhone, HttpStatusCode.Conflict, "account_exists");

        // GAP-P202-01 = C: clearing phone to null when crm.customer_accounts row exists -> 409 account_exists
        using var clearPhone = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/correct-identity", new
        {
            rowVersion = customer.RowVersion,
            phone = (string?)null,
            reason = "Thử xóa SĐT khi đã có tài khoản"
        });
        await AssertProblemAsync(clearPhone, HttpStatusCode.Conflict, "account_exists");

        // Verify phone unchanged
        var unchanged = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{customer.Id}"))!;
        Assert.Equal("+84908888881", unchanged.PhoneE164);

        // Owner changing non-phone identity field (origin) WITHOUT phone key still succeeds
        using var originOnly = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/correct-identity", new
        {
            rowVersion = unchanged.RowVersion,
            origin = "web",
            reason = "Cập nhật nguồn gốc không chạm SĐT"
        });
        Assert.Equal(HttpStatusCode.OK, originOnly.StatusCode);
        var afterOrigin = (await originOnly.Content.ReadFromJsonAsync<CustomerDetailView>())!;
        Assert.Equal("web", afterOrigin.Origin);
        Assert.Equal("+84908888881", afterOrigin.PhoneE164);
    }

    [Fact]
    public async Task Customer360AndOrdersEndpointAggregateCrmDataAndReadSalesOrdersWithoutExposingCostsOrSecrets()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new
        {
            fullName = "Khách 360",
            phone = "0909999111"
        });

        var accountId = Guid.CreateVersion7();
        var orderOldId = Guid.CreateVersion7();
        var orderNewId = Guid.CreateVersion7();
        var materialId = Guid.CreateVersion7();

        await h.ExecuteAsync($"""
INSERT INTO crm.customer_accounts(id,customer_id,login_phone,password_hash,email,phone_verified_at,history_unlocked_at,status,security_stamp)
VALUES ('{accountId}','{customer.Id}','+84909999111','SECRET_HASH_DO_NOT_EXPOSE','c360@example.test','2026-11-20T01:00:00Z','2026-11-20T01:05:00Z','active','SECRET_STAMP_DO_NOT_EXPOSE');
INSERT INTO crm.customer_interests(id,customer_id,interest_type,ref_id,price_band,source,score)
VALUES ('{Guid.CreateVersion7()}','{customer.Id}','material','{materialId}',NULL,'order',4);
INSERT INTO crm.consents(id,customer_id,channel,purpose,status,source,evidence,captured_at)
VALUES ('{Guid.CreateVersion7()}','{customer.Id}','email','marketing_promotions','granted','checkout','SECRET_EVIDENCE_TEXT','2026-11-20T02:00:00Z');
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,customer_account_id,status,payment_method,payment_status,
 contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,
 subtotal_vnd,discount_vnd,shipping_fee_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at,delivered_at) VALUES
 ('{orderOldId}','TP261120AA01','web','web','{customer.Id}','{accountId}','delivered','cod','paid',
  'Khách 360','+84909999111','Khách 360','+84909999111','79','TP HCM',NULL,'Phường 1','1 Lê Lợi',
  1000000,0,30000,1030000,'direct',true,'2026-11-20T03:00:00Z','2026-11-21T03:00:00Z'),
 ('{orderNewId}','TP261122BB02','admin','zalo_personal','{customer.Id}','{accountId}','confirmed','bank_transfer','paid',
  'Khách 360','+84909999111','Khách 360','+84909999111','79','TP HCM',NULL,'Phường 1','1 Lê Lợi',
  2000000,100000,0,1900000,'zalo_personal',false,'2026-11-22T05:00:00Z',NULL);
""");

        var detailRaw = await h.Client.GetStringAsync($"{CustomersRoot}/{customer.Id}");
        AssertNoForbiddenJsonFieldsOrValues(detailRaw);
        var detail = JsonSerializer.Deserialize<CustomerDetailView>(detailRaw, WebJson)!;
        Assert.True(detail.AccountSummary.HasAccount);
        Assert.Equal("active", detail.AccountSummary.Status);
        Assert.Single(detail.Interests);
        Assert.Single(detail.Consents);

        var ordersRaw = await h.Client.GetStringAsync($"{CustomersRoot}/{customer.Id}/orders?page=1&pageSize=10");
        AssertNoForbiddenJsonFieldsOrValues(ordersRaw);
        var orders = JsonSerializer.Deserialize<CatalogPage<CustomerOrderSummaryView>>(ordersRaw, WebJson)!;
        Assert.Equal(2L, orders.TotalItems);
        Assert.Equal([orderNewId, orderOldId], orders.Items.Select(x => x.Id).ToArray());
        Assert.Equal(1900000L, orders.Items[0].TotalVnd);

        // Non-existent customer returns 404 on /orders
        using var missingOrders = await h.Client.GetAsync($"{CustomersRoot}/{Guid.CreateVersion7()}/orders");
        await AssertProblemAsync(missingOrders, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ErasedOrMergedCustomerRejectsProfileTagAddressAndIdentityMutations()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var keeper = await CreateCustomerAsync(h, new { fullName = "Khách Giữ", phone = "0901010101" });
        var erased = await CreateCustomerAsync(h, new { fullName = "Khách Xóa", phone = "0901010102" });
        var merged = await CreateCustomerAsync(h, new { fullName = "Khách Gộp", phone = "0901010103" });

        await h.ExecuteAsync($"""
UPDATE crm.customers SET contact_status = 'erased', erased_at = now(), full_name = '[Đã xóa]', phone_e164 = NULL, zalo_name = NULL, search_text = '' WHERE id = '{erased.Id}';
UPDATE crm.customers SET merged_into_customer_id = '{keeper.Id}', phone_e164 = NULL WHERE id = '{merged.Id}';
""");

        var erasedFresh = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{erased.Id}"))!;
        var mergedFresh = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{merged.Id}"))!;

        foreach (var (target, expectedCode) in new[] { (erasedFresh, "customer_erased"), (mergedFresh, "customer_merged") })
        {
            using var p = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{target.Id}", new { rowVersion = target.RowVersion, fullName = "Ghi Đè" });
            await AssertProblemAsync(p, HttpStatusCode.Conflict, expectedCode);

            using var ci = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{target.Id}/correct-identity", new { rowVersion = target.RowVersion, phone = "0901010199", reason = "Thử sửa hồ sơ đã khóa" });
            await AssertProblemAsync(ci, HttpStatusCode.Conflict, expectedCode);

            using var tags = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{target.Id}/tags", new { tagIds = Array.Empty<Guid>() });
            await AssertProblemAsync(tags, HttpStatusCode.Conflict, expectedCode);

            using var addr = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{target.Id}/addresses", new
            {
                receiverName = "Người Nhận",
                receiverPhone = "0901010199",
                provinceCode = "79",
                wardNameText = "Phường 1",
                addressLine = "1 Lê Lợi"
            });
            await AssertProblemAsync(addr, HttpStatusCode.Conflict, expectedCode);
        }
    }

    [Fact]
    public async Task FailedValidationOrConflictRollsBackSequenceSideEffectsAndLeavesZeroOrphanRows()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var existing = await CreateCustomerAsync(h, new { fullName = "Khách Tồn Tại", phone = "0902020202" });
        using var tagResp = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Tag Hợp Lệ" });
        var tag = (await tagResp.Content.ReadFromJsonAsync<CustomerTagView>())!;

        // Failing creation with unknown tagId must leave zero customer / customer_tags rows
        using var unknownTag = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Sẽ Rollback",
            phone = "0902020203",
            tagIds = new[] { tag.Id, Guid.CreateVersion7() }
        });
        await AssertProblemAsync(unknownTag, HttpStatusCode.BadRequest, "validation_failed");

        // Failing creation with duplicate phone must leave zero extra customer / customer_tags rows
        using var dupPhone = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = "Sẽ Rollback Trùng",
            phone = "0902020202",
            tagIds = new[] { tag.Id }
        });
        await AssertProblemAsync(dupPhone, HttpStatusCode.Conflict, "customer_phone_exists");

        await using var db = h.OwnerContext();
        Assert.Equal(1, await db.Set<Customer>().CountAsync());
        Assert.Equal(existing.Id, (await db.Set<Customer>().SingleAsync()).Id);
        Assert.Empty(await db.Set<CustomerTag>().ToArrayAsync());
    }

    internal const string StaffInitialPassword = "Disposable-Staff-Init-2026!";
    internal const string StaffNewPassword = "Disposable-Staff-Ready-2026!";

    internal static async Task<string> CreateStaffAsync(P009Harness h, string role, bool ready = true)
    {
        var email = $"{role}-{Guid.CreateVersion7():N}@example.test";
        using var create = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/staff", new
        {
            email,
            displayName = $"Test {role}",
            initialPassword = StaffInitialPassword,
            roles = new[] { role }
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        if (ready)
        {
            using var tempClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await LoginClientAsync(tempClient, email, StaffInitialPassword);
            var csrf = (await tempClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/auth/change-password")
            {
                Content = JsonContent.Create(new { currentPassword = StaffInitialPassword, newPassword = StaffNewPassword })
            };
            req.Headers.Add("X-CSRF-TOKEN", csrf);
            using var change = await tempClient.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        return email;
    }

    internal static async Task LoginClientAsync(HttpClient client, string email, string password)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    internal static async Task<CustomerDetailView> CreateCustomerAsync(P009Harness h, object payload)
    {
        using var resp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, payload);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, $"Expected 201 Created, got {(int)resp.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerDetailView>(body, WebJson)!;
    }

    internal static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatus, $"Expected {(int)expectedStatus}, got {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
        return doc.RootElement.Clone();
    }

    private static void AssertNoForbiddenJsonFieldsOrValues(string rawJson)
    {
        Assert.DoesNotContain("SECRET_HASH_DO_NOT_EXPOSE", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_STAMP_DO_NOT_EXPOSE", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_EVIDENCE_TEXT", rawJson, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(rawJson);
        CheckElement(doc.RootElement);

        static void CheckElement(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    Assert.DoesNotContain("password", prop.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("securityStamp", prop.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("unitCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("avgCost", prop.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("grossProfit", prop.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("evidence", prop.Name, StringComparison.OrdinalIgnoreCase);
                    CheckElement(prop.Value);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    CheckElement(item);
                }
            }
        }
    }
}
