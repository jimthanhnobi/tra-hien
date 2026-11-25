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
using System.Reflection;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Crm;
using TeaPremium.Infrastructure.Crm;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CustomersApiTests;

namespace TeaPremium.IntegrationTests;

public sealed class CustomerTagsAddressesAndSecurityTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string AdminCsvHeader = "province_code,province_name,ward_code,ward_name\n";

    [Fact]
    public async Task TagsCrudCreatesUpdatesListsAndRejectsDuplicateNameWith409()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        using var createA = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "  Trà Cao Cấp ", color = "#2F5233" });
        Assert.Equal(HttpStatusCode.Created, createA.StatusCode);
        var tagA = (await createA.Content.ReadFromJsonAsync<CustomerTagView>())!;
        Assert.Equal("Trà Cao Cấp", tagA.Name);
        Assert.Equal("#2F5233", tagA.Color);

        using var createB = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Ấm Tử Sa", color = (string?)null });
        Assert.Equal(HttpStatusCode.Created, createB.StatusCode);
        var tagB = (await createB.Content.ReadFromJsonAsync<CustomerTagView>())!;

        // Duplicate name -> 409 tag_name_exists
        using var dupCreate = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Trà Cao Cấp" });
        await AssertProblemAsync(dupCreate, HttpStatusCode.Conflict, "tag_name_exists");

        // PATCH /api/v1/admin/tags/{id}
        using var patchA = await h.MutateAsync(HttpMethod.Patch, $"{TagsRoot}/{tagA.Id}", new { color = (string?)null });
        Assert.Equal(HttpStatusCode.OK, patchA.StatusCode);
        var patched = (await patchA.Content.ReadFromJsonAsync<CustomerTagView>())!;
        Assert.Equal("Trà Cao Cấp", patched.Name);
        Assert.Null(patched.Color);

        using var dupPatch = await h.MutateAsync(HttpMethod.Patch, $"{TagsRoot}/{tagB.Id}", new { name = "Trà Cao Cấp" });
        await AssertProblemAsync(dupPatch, HttpStatusCode.Conflict, "tag_name_exists");

        // List sorted by name ASC, id ASC
        var all = (await h.Client.GetFromJsonAsync<CustomerTagView[]>(TagsRoot))!;
        Assert.Equal(2, all.Length);
        Assert.Contains(all, x => x.Id == tagA.Id);
        Assert.Contains(all, x => x.Id == tagB.Id);

        // Non-JSON Content-Type -> 415
        var csrf = (await h.Client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        using var wrongCt = new HttpRequestMessage(HttpMethod.Post, TagsRoot)
        {
            Content = new StringContent("name=plain", Encoding.UTF8, "text/plain")
        };
        wrongCt.Headers.Add("X-CSRF-TOKEN", csrf);
        using var ctResp = await h.Client.SendAsync(wrongCt);
        await AssertProblemAsync(ctResp, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task PutCustomerTagsReplacesSetIdempotentlyAdvancesCustomerRowVersionAndRejectsUnknownTag()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var c = await CreateCustomerAsync(h, new { fullName = "Khách Tag", phone = "0903030301" });
        var t1 = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "VIP" })).Content.ReadFromJsonAsync<CustomerTagView>())!;
        var t2 = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Quà Tặng" })).Content.ReadFromJsonAsync<CustomerTagView>())!;

        using var put1 = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = new[] { t1.Id, t2.Id, t1.Id }, rowVersion = c.RowVersion });
        Assert.Equal(HttpStatusCode.OK, put1.StatusCode);
        var r1 = (await put1.Content.ReadFromJsonAsync<CustomerTagsResult>())!;
        Assert.Equal(2, r1.Tags.Count);
        Assert.NotEqual(c.RowVersion, r1.RowVersion);

        // Stale rowVersion rejected
        using var stale = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = new[] { t1.Id }, rowVersion = c.RowVersion });
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");

        // Idempotent repeat without rowVersion succeeds and advances rowVersion
        using var put2 = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = new[] { t1.Id, t2.Id } });
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
        var r2 = (await put2.Content.ReadFromJsonAsync<CustomerTagsResult>())!;
        Assert.Equal(2, r2.Tags.Count);
        Assert.NotEqual(r1.RowVersion, r2.RowVersion);

        // Clear all tags
        using var putEmpty = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, putEmpty.StatusCode);
        var rEmpty = (await putEmpty.Content.ReadFromJsonAsync<CustomerTagsResult>())!;
        Assert.Empty(rEmpty.Tags);

        // Unknown tag rejected
        using var unknown = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = new[] { Guid.CreateVersion7() } });
        await AssertProblemAsync(unknown, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task CustomerAddressesCrudValidatesActiveProvinceWardViaAdministrativePortAndSupportsWardNameFallback()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var c = await CreateCustomerAsync(h, new { fullName = "Khách Địa Chỉ", phone = "0904040401" });

        // Before administrative import -> 422 administrative_data_not_ready
        using var notReady = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            receiverName = "Nguyễn A",
            receiverPhone = "0904040402",
            provinceCode = "79",
            wardCode = "79001",
            addressLine = "10 Lê Lợi"
        });
        await AssertProblemAsync(notReady, HttpStatusCode.UnprocessableEntity, "administrative_data_not_ready");

        // Import valid provinces and wards
        await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(1, AdminCsvHeader
            + "01,Hà Nội,,\n01,Hà Nội,00001,Phường Hoàn Kiếm\n79,TP Hồ Chí Minh,,\n79,TP Hồ Chí Minh,79001,Phường Bến Nghé\n"));

        // Valid active province + ward -> 201
        using var createOk = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            label = "Công ty",
            receiverName = "Nguyễn A",
            receiverPhone = "0904-040-402",
            provinceCode = "79",
            wardCode = "79001",
            addressLine = "10 Lê Lợi",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, createOk.StatusCode);
        var addr1 = (await createOk.Content.ReadFromJsonAsync<CustomerAddressView>())!;
        Assert.Equal("+84904040402", addr1.ReceiverPhone);
        Assert.Equal("TP Hồ Chí Minh", addr1.ProvinceName);
        Assert.Equal("Phường Bến Nghé", addr1.WardName);
        Assert.True(addr1.IsDefault);
        Assert.True(addr1.IsLocationActive);

        // Wrong province/ward pair -> 400 invalid_address_location
        using var mismatch = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            receiverName = "Nguyễn A",
            receiverPhone = "0904040402",
            provinceCode = "01",
            wardCode = "79001",
            addressLine = "10 Lê Lợi"
        });
        await AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "invalid_address_location");

        // Fallback wardNameText with wardCode = null -> 201
        using var fallbackResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            receiverName = "Nguyễn B",
            receiverPhone = "0904040403",
            provinceCode = "01",
            wardCode = (string?)null,
            wardNameText = "Phường Cũ Dự Phòng",
            addressLine = "20 Hàng Đào",
            isDefault = false
        });
        Assert.Equal(HttpStatusCode.Created, fallbackResp.StatusCode);
        var addr2 = (await fallbackResp.Content.ReadFromJsonAsync<CustomerAddressView>())!;
        Assert.Null(addr2.WardCode);
        Assert.Equal("Phường Cũ Dự Phòng", addr2.WardNameText);
        Assert.True(addr2.IsLocationActive);

        // GET list and single
        var list = (await h.Client.GetFromJsonAsync<CustomerAddressView[]>($"{CustomersRoot}/{c.Id}/addresses"))!;
        Assert.Equal([addr1.Id, addr2.Id], list.Select(x => x.Id).ToArray());

        var single = (await h.Client.GetFromJsonAsync<CustomerAddressView>($"{CustomersRoot}/{c.Id}/addresses/{addr2.Id}"))!;
        Assert.Equal(addr2.Id, single.Id);

        // DELETE without body -> 204
        var csrf = (await h.Client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{CustomersRoot}/{c.Id}/addresses/{addr2.Id}");
        delReq.Headers.Add("X-CSRF-TOKEN", csrf);
        using var delResp = await h.Client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var remaining = (await h.Client.GetFromJsonAsync<CustomerAddressView[]>($"{CustomersRoot}/{c.Id}/addresses"))!;
        Assert.Single(remaining);
    }

    [Fact]
    public async Task CustomerAddressesSwitchingDefaultAcrossMultipleAddressesNeverViolatesPartialUniqueIndex()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(1, AdminCsvHeader + "79,TP Hồ Chí Minh,,\n79,TP Hồ Chí Minh,79001,Phường Bến Nghé\n"));

        var c = await CreateCustomerAsync(h, new { fullName = "Khách Nhiều Địa Chỉ", phone = "0905050501" });

        var ids = new List<Guid>(3);
        for (var i = 0; i < 3; i++)
        {
            using var resp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
            {
                receiverName = $"Người Nhận {i}",
                receiverPhone = $"090505050{i + 2}",
                provinceCode = "79",
                wardCode = "79001",
                addressLine = $"Số {i + 1} Lê Lợi",
                isDefault = true
            });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            ids.Add((await resp.Content.ReadFromJsonAsync<CustomerAddressView>())!.Id);
        }

        // Toggle back to first address sequentially
        using var patchFirst = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{c.Id}/addresses/{ids[0]}", new { isDefault = true });
        Assert.Equal(HttpStatusCode.OK, patchFirst.StatusCode);

        // Concurrent storm: 5 parallel PATCH requests toggling default across the 3 addresses
        var clients = new List<HttpClient>(5);
        var tokens = new List<string>(5);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var client = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await LoginClientAsync(client, "owner@example.test", P009Harness.Password);
                var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
                clients.Add(client);
                tokens.Add(csrf);
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 5).Select(async idx =>
            {
                await barrier.Task;
                var targetId = ids[idx % ids.Count];
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{CustomersRoot}/{c.Id}/addresses/{targetId}")
                {
                    Content = JsonContent.Create(new { isDefault = true })
                };
                req.Headers.Add("X-CSRF-TOKEN", tokens[idx]);
                using var resp = await clients[idx].SendAsync(req);
                return resp.StatusCode;
            }).ToArray();

            barrier.SetResult();
            var statuses = await Task.WhenAll(tasks);
            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }

        var addresses = (await h.Client.GetFromJsonAsync<CustomerAddressView[]>($"{CustomersRoot}/{c.Id}/addresses"))!;
        Assert.Equal(1, addresses.Count(x => x.IsDefault));
    }

    [Fact]
    public async Task CustomerAddressesLegacyReparentedWardIsPreservedOnNonLocationPatchAndFlaggedInactiveOnRead()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var e1 = await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(1, AdminCsvHeader
            + "01,Hà Nội,,\n01,Hà Nội,00001,Phường Cũ\n79,TP Hồ Chí Minh,,\n"));
        var c = await CreateCustomerAsync(h, new { fullName = "Khách Địa Giới Cũ", phone = "0906060601" });

        using var create = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            receiverName = "Người Cũ",
            receiverPhone = "0906060602",
            provinceCode = "01",
            wardCode = "00001",
            addressLine = "5 Hàng Bạc"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var addr = (await create.Content.ReadFromJsonAsync<CustomerAddressView>())!;
        Assert.True(addr.IsLocationActive);

        // Subsequent full import deactivates ward 00001
        await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(2, AdminCsvHeader + "01,Hà Nội,,\n79,TP Hồ Chí Minh,,\n", e1));

        // Reading address flags isLocationActive = false without rewriting saved wardCode
        var afterDeactivate = (await h.Client.GetFromJsonAsync<CustomerAddressView>($"{CustomersRoot}/{c.Id}/addresses/{addr.Id}"))!;
        Assert.Equal("01", afterDeactivate.ProvinceCode);
        Assert.Equal("00001", afterDeactivate.WardCode);
        Assert.False(afterDeactivate.IsLocationActive);

        // Non-location PATCH (receiverName only) preserves legacy codes (03-database.md §3.6)
        using var patchName = await h.MutateAsync(HttpMethod.Patch, $"{CustomersRoot}/{c.Id}/addresses/{addr.Id}", new
        {
            receiverName = "Người Mới"
        });
        Assert.Equal(HttpStatusCode.OK, patchName.StatusCode);
        var patched = (await patchName.Content.ReadFromJsonAsync<CustomerAddressView>())!;
        Assert.Equal("Người Mới", patched.ReceiverName);
        Assert.Equal("01", patched.ProvinceCode);
        Assert.Equal("00001", patched.WardCode);
        Assert.False(patched.IsLocationActive);
    }

    [Fact]
    public async Task AcP209ContentRoleUnauthenticatedUnreadyHttpAndMissingCsrfAreRejectedAcrossAllEndpoints()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var customer = await CreateCustomerAsync(h, new { fullName = "Khách Bảo Mật", phone = "0907070701" });
        var tag = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Tag Bảo Mật" })).Content.ReadFromJsonAsync<CustomerTagView>())!;
        var addrId = Guid.CreateVersion7();

        var contentEmail = await CreateStaffAsync(h, "content", ready: true);
        var unreadySalesEmail = await CreateStaffAsync(h, "sales", ready: false);

        var endpoints = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, CustomersRoot, null),
            (HttpMethod.Post, CustomersRoot, new { fullName = "X", phone = "0907070702" }),
            (HttpMethod.Get, $"{CustomersRoot}/{customer.Id}", null),
            (HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}", new { rowVersion = customer.RowVersion, fullName = "Y" }),
            (HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/correct-identity", new { rowVersion = customer.RowVersion, phone = "0907070703", reason = "test" }),
            (HttpMethod.Get, $"{CustomersRoot}/{customer.Id}/orders", null),
            (HttpMethod.Put, $"{CustomersRoot}/{customer.Id}/tags", new { tagIds = Array.Empty<Guid>() }),
            (HttpMethod.Get, $"{CustomersRoot}/{customer.Id}/addresses", null),
            (HttpMethod.Get, $"{CustomersRoot}/{customer.Id}/addresses/{addrId}", null),
            (HttpMethod.Post, $"{CustomersRoot}/{customer.Id}/addresses", new { receiverName = "A", receiverPhone = "0907070702", provinceCode = "79", wardNameText = "P1", addressLine = "1 St" }),
            (HttpMethod.Patch, $"{CustomersRoot}/{customer.Id}/addresses/{addrId}", new { receiverName = "B" }),
            (HttpMethod.Delete, $"{CustomersRoot}/{customer.Id}/addresses/{addrId}", null),
            (HttpMethod.Get, TagsRoot, null),
            (HttpMethod.Post, TagsRoot, new { name = "New" }),
            (HttpMethod.Patch, $"{TagsRoot}/{tag.Id}", new { name = "Renamed" })
        };
        Assert.Equal(15, endpoints.Length);

        // 1. Unauthenticated -> 401 on all 15 endpoints
        using (var anon = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            foreach (var (method, path, body) in endpoints)
            {
                using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                using var resp = await anon.SendAsync(req);
                await AssertProblemAsync(resp, HttpStatusCode.Unauthorized, "unauthenticated");
            }
        }

        // 2. Content role -> 403 forbidden on all 15 endpoints (AC-P2-09)
        using (var contentClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(contentClient, contentEmail, StaffNewPassword);
            var csrf = (await contentClient.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            foreach (var (method, path, body) in endpoints)
            {
                using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                if (method != HttpMethod.Get) req.Headers.Add("X-CSRF-TOKEN", csrf);
                using var resp = await contentClient.SendAsync(req);
                await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
            }
        }

        // 3. Staff with MustChangePassword -> 403 forbidden
        using (var unreadyClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(unreadyClient, unreadySalesEmail, StaffInitialPassword);
            using var resp = await unreadyClient.GetAsync(CustomersRoot);
            await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
        }

        // 4. Plain HTTP -> 400 https_required
        using (var httpClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost"), AllowAutoRedirect = false }))
        {
            using var resp = await httpClient.GetAsync(CustomersRoot);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "https_required");
        }

        // 5. Missing CSRF on every mutating endpoint -> 400 csrf_invalid
        foreach (var (method, path, body) in endpoints.Where(e => e.Method != HttpMethod.Get))
        {
            using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            using var resp = await h.Client.SendAsync(req);
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "csrf_invalid");
        }
    }

    [Fact]
    public async Task AuditLogsRecordCustomerAddressTagAndAccountChangesWithZeroRawPiiOrSearchTextLeak()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(1, AdminCsvHeader + "79,TP Hồ Chí Minh,,\n79,TP Hồ Chí Minh,79001,Phường Bến Nghé\n"));

        const string secretName = "Đặng Văn Thử";
        const string secretNameUnaccent = "dang van thu";
        const string secretPhoneLocal = "0918765432";
        const string secretPhoneE164 = "+84918765432";
        const string secretNewPhoneE164 = "+84918765499";
        const string secretEmail = "dang.thu@example.test";
        const string secretZalo = "Zalo Bí Mật 99";
        const string secretAddress = "123 Nguyễn Huệ Phòng 909";
        const string secretTag = "Nhãn Riêng Đặng Thử";
        const string secretReason = "Đổi từ số 0918765432 sang số mới 0918765499";

        var created = await CreateCustomerAsync(h, new
        {
            fullName = secretName,
            phone = secretPhoneLocal,
            email = secretEmail,
            zaloName = secretZalo,
            noteSummary = "Ghi chú riêng 0918765432"
        });

        using (var tagResp = await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = secretTag, color = "#112233" }))
            Assert.Equal(HttpStatusCode.Created, tagResp.StatusCode);

        using (var addrResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{created.Id}/addresses", new
        {
            label = "Nhà Đặng Thử",
            receiverName = secretName,
            receiverPhone = secretPhoneLocal,
            provinceCode = "79",
            wardCode = "79001",
            addressLine = secretAddress,
            isDefault = true
        }))
            Assert.Equal(HttpStatusCode.Created, addrResp.StatusCode);

        var refreshed = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{created.Id}"))!;
        using (var correctResp = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{created.Id}/correct-identity", new
        {
            rowVersion = refreshed.RowVersion,
            phone = "0918765499",
            reason = secretReason
        }))
            Assert.Equal(HttpStatusCode.OK, correctResp.StatusCode);

        await using var db = h.OwnerContext();
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "crm.customers" || x.EntityType == "crm.customer_addresses" || x.EntityType == "crm.tags")
            .ToArrayAsync();

        Assert.Contains(logs, x => x.EntityType == "crm.customers" && x.Action == "create");
        Assert.Contains(logs, x => x.EntityType == "crm.customers" && x.Action == "update");
        Assert.Contains(logs, x => x.EntityType == "crm.customers" && x.Action == "state_change");
        Assert.Contains(logs, x => x.EntityType == "crm.customer_addresses" && x.Action == "create");
        Assert.Contains(logs, x => x.EntityType == "crm.tags" && x.Action == "create");

        var sensitiveNeedles = new[]
        {
            secretName, secretNameUnaccent, secretPhoneLocal, secretPhoneE164,
            secretNewPhoneE164, secretEmail, secretZalo, secretAddress, secretTag, secretReason, "0918765499"
        };

        foreach (var log in logs)
        {
            foreach (var needle in sensitiveNeedles)
            {
                Assert.DoesNotContain(needle, log.Changes, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Verify encrypted reason decrypts cleanly with the exact DataProtection purpose
        var stateChange = logs.Single(x => x.EntityType == "crm.customers" && x.Action == "state_change");
        using var changesDoc = JsonDocument.Parse(stateChange.Changes);
        Assert.True(changesDoc.RootElement.GetProperty("phone_changed").GetBoolean());
        Assert.Equal("data_protection_v1", changesDoc.RootElement.GetProperty("reason_format").GetString());
        var protectedReason = changesDoc.RootElement.GetProperty("reason").GetString()!;

        await using var scope = h.Factory.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(CustomerIdentityAudit.ReasonProtectorPurpose);
        Assert.Equal(secretReason, protector.Unprotect(protectedReason));
    }

    [Fact]
    public async Task ProblemDetailsAndStructuredLogsNeverEchoSubmittedPhoneEmailNameOrAddress()
    {
        var capturedLogs = new ConcurrentQueue<string>();
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogs)));
        });
        await h.LoginAsync();

        const string badPhone = "02439998877";
        const string badEmail = "leak-check-email";
        const string secretName = "Nguyễn Không Lộ Log";
        const string dupPhone = "0908123456";

        await CreateCustomerAsync(h, new { fullName = "Gốc", phone = dupPhone });

        using var badPhoneResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = secretName,
            phone = badPhone
        });
        var badPhoneBody = await badPhoneResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badPhoneResp.StatusCode);

        using var badEmailResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = secretName,
            phone = "0908123457",
            email = badEmail
        });
        var badEmailBody = await badEmailResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badEmailResp.StatusCode);

        using var dupResp = await h.MutateAsync(HttpMethod.Post, CustomersRoot, new
        {
            fullName = secretName,
            phone = dupPhone
        });
        var dupBody = await dupResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        var combinedOutput = string.Join("\n", badPhoneBody, badEmailBody, dupBody, string.Join("\n", capturedLogs));
        Assert.DoesNotContain(badPhone, combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(badEmail, combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secretName, combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(dupPhone, combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("+84908123456", combinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchitectureRulesRemainGreenForCrmAndOrdersReadContracts()
    {
        var forbiddenTypes = new HashSet<Type> { typeof(Province), typeof(Ward), typeof(Order) };
        var crmInfraTypes = typeof(EfCustomerStore).Assembly.GetTypes()
            .Where(t => t.Namespace == "TeaPremium.Infrastructure.Crm"
                || (t.DeclaringType?.Namespace == "TeaPremium.Infrastructure.Crm"))
            .ToArray();
        Assert.NotEmpty(crmInfraTypes);

        foreach (var type in crmInfraTypes)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
            {
                AssertDoesNotContainForbiddenType(field.FieldType, forbiddenTypes, $"{type.FullName}.{field.Name}");
            }

            foreach (var method in type.GetMethods(flags))
            {
                AssertDoesNotContainForbiddenType(method.ReturnType, forbiddenTypes, $"{type.FullName}.{method.Name}");
                foreach (var param in method.GetParameters())
                {
                    AssertDoesNotContainForbiddenType(param.ParameterType, forbiddenTypes, $"{type.FullName}.{method.Name}({param.Name})");
                }
            }
        }

        var appRefs = typeof(ICustomerStore).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(appRefs, name => name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appRefs, name => name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appRefs, name => name.StartsWith("TeaPremium.Infrastructure", StringComparison.Ordinal));
    }

    private static void AssertDoesNotContainForbiddenType(Type candidate, HashSet<Type> forbidden, string site)
    {
        Assert.False(forbidden.Contains(candidate), $"Forbidden type {candidate.Name} referenced at {site}");
        if (candidate.IsGenericType)
        {
            foreach (var arg in candidate.GetGenericArguments())
            {
                AssertDoesNotContainForbiddenType(arg, forbidden, site);
            }
        }
        else if (candidate.HasElementType && candidate.GetElementType() is { } element)
        {
            AssertDoesNotContainForbiddenType(element, forbidden, site);
        }
    }

    [Fact]
    public async Task RealTpAppDatabaseRoleExecutesAllCrmEndpointsAndCannotHardDeleteCustomer()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();
        await ApplyAdminBundleAsync(h, P009ImportTests.Bundle(1, AdminCsvHeader + "79,TP Hồ Chí Minh,,\n79,TP Hồ Chí Minh,79001,Phường Bến Nghé\n"));

        // P009Harness runs API on tp_app runtime connection; exercise full CRUD + tags + address + correct-identity
        var c = await CreateCustomerAsync(h, new { fullName = "Khách Role TpApp", phone = "0909090901" });
        var tag = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Tag TpApp" })).Content.ReadFromJsonAsync<CustomerTagView>())!;
        using (var putTags = await h.MutateAsync(HttpMethod.Put, $"{CustomersRoot}/{c.Id}/tags", new { tagIds = new[] { tag.Id } }))
            Assert.Equal(HttpStatusCode.OK, putTags.StatusCode);
        using (var addAddr = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/addresses", new
        {
            receiverName = "Người Nhận",
            receiverPhone = "0909090902",
            provinceCode = "79",
            wardCode = "79001",
            addressLine = "1 Lê Lợi",
            isDefault = true
        }))
            Assert.Equal(HttpStatusCode.Created, addAddr.StatusCode);

        var fresh = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{c.Id}"))!;
        using (var ci = await h.MutateAsync(HttpMethod.Post, $"{CustomersRoot}/{c.Id}/correct-identity", new
        {
            rowVersion = fresh.RowVersion,
            phone = "0909090903",
            reason = "Xác nhận đổi số dưới quyền tp_app"
        }))
            Assert.Equal(HttpStatusCode.OK, ci.StatusCode);

        // Hard DELETE on crm.customers under real tp_app role must be denied by PostgreSQL (42501)
        await using var conn = new NpgsqlConnection(h.RuntimeConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DELETE FROM crm.customers WHERE id = '{c.Id}'", conn);
        var pgEx = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pgEx.SqlState);
    }

    private static async Task<Guid> ApplyAdminBundleAsync(P009Harness h, TeaPremium.Application.AdministrativeData.ImportBundle bundle)
    {
        var plan = await h.PlanAsync(bundle);
        var result = await h.ApplyAsync(bundle, h.Approve(bundle, plan));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        return result.Value.EventId!.Value;
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
