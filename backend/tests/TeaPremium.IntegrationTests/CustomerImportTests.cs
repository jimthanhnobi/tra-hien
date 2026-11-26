using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Crm;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CustomersApiTests;

namespace TeaPremium.IntegrationTests;

public sealed class CustomerImportTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string ImportRoot = "/api/v1/admin/customers/import";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] FullHeaders = ["full_name", "phone", "zalo_name", "customer_group", "tags", "note", "had_purchase"];
    private static readonly string[] ShortHeaders = ["full_name", "phone"];
    private static readonly string[] TagNoteHeaders = ["full_name", "phone", "tags", "note"];

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task AcP208Import500RowsWith3InFileDuplicatesAnd2ExistingCustomersReportsExactReasonsAndLeavesExistingProfilesUntouched(string format)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // Create 2 pre-existing active customers with distinct attributes and tag
        var tagPre = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Khách Cũ Gốc", color = "#112233" }))
            .Content.ReadFromJsonAsync<CustomerTagView>())!;
        var existing1 = await CreateCustomerAsync(h, new
        {
            fullName = "Hồ Sơ Gốc Một",
            phone = "0901000001",
            customerGroup = "wholesale",
            noteSummary = "Ghi chú gốc 1",
            tagIds = new[] { tagPre.Id }
        });
        var existing2 = await CreateCustomerAsync(h, new
        {
            fullName = "Hồ Sơ Gốc Hai",
            phone = "0901000002",
            customerGroup = "reseller",
            noteSummary = "Ghi chú gốc 2"
        });

        // Build 500 data rows:
        // - 2 existing DB phones (rows 2 & 3)
        // - 3 in-file duplicates (matching rows 4, 5, 6 in mixed phone formats)
        // - 495 brand new customers
        var tableRows = new List<string[]>(501) { FullHeaders };

        tableRows.Add(["Ghi Đè Thử 1", "0901.000.001", "Zalo Đè 1", "retail", "VIP Mới", "Note đè 1", "false"]);
        tableRows.Add(["Ghi Đè Thử 2", "+84901000002", "Zalo Đè 2", "retail", "VIP Mới", "Note đè 2", "false"]);

        for (var i = 1; i <= 495; i++)
        {
            var phone = $"091{i:D7}";
            var group = i % 50 == 0 ? "wholesale" : "";
            var tags = i <= 10 ? "VIP; Ấm Tử Sa" : (i == 11 ? "khách cũ gốc" : "");
            var note = i <= 5 ? $"Ghi chú khách cũ số {i}" : "";
            var hadPurchase = i == 2 ? "false" : "";
            tableRows.Add([$"Khách Nhập {i:D3}", phone, "", group, tags, note, hadPurchase]);
        }

        tableRows.Add(["Trùng Trong File 1", "+84910000001", "", "", "", "", ""]);
        tableRows.Add(["Trùng Trong File 2", "84910-000-002", "", "", "", "", ""]);
        tableRows.Add(["Trùng Trong File 3", "0910 000 003", "", "", "", "", ""]);

        Assert.Equal(501, tableRows.Count);

        var (bytes, filename, mediaType) = format == "csv"
            ? (BuildCsvBytes(tableRows), "ac-p2-08.csv", "text/csv")
            : (BuildXlsxBytes("KhachHang", tableRows), "ac-p2-08.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var beforeCounts = await ReadDbSnapshotAsync(h.OwnerConnection);

        // Step 1: dryRun=true must NOT modify DB or consume customer_code_seq
        using (var dryResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", bytes, filename, mediaType))
        {
            var body = await dryResp.Content.ReadAsStringAsync();
            Assert.True(dryResp.StatusCode == HttpStatusCode.OK, body);
            var dryResult = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

            Assert.True(dryResult.Summary.DryRun);
            Assert.False(dryResult.Summary.Committed);
            Assert.Equal(500, dryResult.Summary.TotalRows);
            Assert.Equal(495, dryResult.Summary.ReadyCount);
            Assert.Equal(0, dryResult.Summary.ImportedCount);
            Assert.Equal(5, dryResult.Summary.SkippedCount);
            Assert.Equal(3, dryResult.Summary.DuplicateInFileCount);
            Assert.Equal(2, dryResult.Summary.ExistingPhoneSkippedCount);
            Assert.Equal(0, dryResult.Summary.ErrorCount);
            Assert.Equal(0, dryResult.Summary.CreatedTagsCount);
            Assert.Equal(0, dryResult.Summary.CreatedInteractionsCount);
            Assert.Equal(["VIP", "Ấm Tử Sa"], dryResult.Summary.NewTags);

            Assert.Equal("existing_phone_skipped", dryResult.Rows[0].ReasonCode);
            Assert.Equal(existing1.Id, dryResult.Rows[0].ExistingCustomerId);
            Assert.Equal("existing_phone_skipped", dryResult.Rows[1].ReasonCode);
            Assert.Equal(existing2.Id, dryResult.Rows[1].ExistingCustomerId);

            Assert.Equal("would_import", dryResult.Rows[2].Status);
            Assert.Null(dryResult.Rows[2].CustomerId);
            Assert.Null(dryResult.Rows[2].CustomerCode);
            Assert.True(dryResult.Rows[2].CreateNoteInteraction);
            Assert.Equal(["VIP", "Ấm Tử Sa"], dryResult.Rows[2].Tags);

            Assert.Equal("duplicate_in_file", dryResult.Rows[497].ReasonCode);
            Assert.Equal(4, dryResult.Rows[497].DuplicateOfRow);
            Assert.Equal("duplicate_in_file", dryResult.Rows[498].ReasonCode);
            Assert.Equal(5, dryResult.Rows[498].DuplicateOfRow);
            Assert.Equal("duplicate_in_file", dryResult.Rows[499].ReasonCode);
            Assert.Equal(6, dryResult.Rows[499].DuplicateOfRow);
        }

        var afterDryCounts = await ReadDbSnapshotAsync(h.OwnerConnection);
        Assert.Equal(beforeCounts, afterDryCounts);

        // Step 2: dryRun=false commits 495 new customers and leaves existing1 & existing2 untouched
        using (var applyResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", bytes, filename, mediaType))
        {
            var body = await applyResp.Content.ReadAsStringAsync();
            Assert.True(applyResp.StatusCode == HttpStatusCode.OK, body);
            var applyResult = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

            Assert.False(applyResult.Summary.DryRun);
            Assert.True(applyResult.Summary.Committed);
            Assert.Equal(500, applyResult.Summary.TotalRows);
            Assert.Equal(495, applyResult.Summary.ReadyCount);
            Assert.Equal(495, applyResult.Summary.ImportedCount);
            Assert.Equal(5, applyResult.Summary.SkippedCount);
            Assert.Equal(3, applyResult.Summary.DuplicateInFileCount);
            Assert.Equal(2, applyResult.Summary.ExistingPhoneSkippedCount);
            Assert.Equal(0, applyResult.Summary.ErrorCount);
            Assert.Equal(2, applyResult.Summary.CreatedTagsCount);
            Assert.Equal(5, applyResult.Summary.CreatedInteractionsCount);
            Assert.Equal(["VIP", "Ấm Tử Sa"], applyResult.Summary.NewTags);
            Assert.Equal(applyResult.Summary.CreatedTagsCount, applyResult.Summary.NewTags.Count);

            var importedRows = applyResult.Rows.Where(r => r.Status == "imported").ToArray();
            Assert.Equal(495, importedRows.Length);
            Assert.All(importedRows, r =>
            {
                Assert.NotNull(r.CustomerId);
                Assert.StartsWith("KH", r.CustomerCode, StringComparison.Ordinal);
            });
            Assert.Equal(495, importedRows.Select(r => r.CustomerCode).Distinct(StringComparer.Ordinal).Count());
        }

        var reloaded1 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{existing1.Id}"))!;
        Assert.Equal(existing1.FullName, reloaded1.FullName);
        Assert.Equal(existing1.CustomerGroup, reloaded1.CustomerGroup);
        Assert.Equal(existing1.NoteSummary, reloaded1.NoteSummary);
        Assert.Equal(existing1.RowVersion, reloaded1.RowVersion);
        Assert.Single(reloaded1.Tags);
        Assert.Equal("Khách Cũ Gốc", reloaded1.Tags[0].Name);

        var reloaded2 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{existing2.Id}"))!;
        Assert.Equal(existing2.FullName, reloaded2.FullName);
        Assert.Equal(existing2.CustomerGroup, reloaded2.CustomerGroup);
        Assert.Equal(existing2.NoteSummary, reloaded2.NoteSummary);
        Assert.Equal(existing2.RowVersion, reloaded2.RowVersion);
        Assert.Empty(reloaded2.Tags);
    }

    [Fact]
    public async Task BrCrm03BrCrm07BrCrm14ApplyImportSetsLegacyOriginAllocatesKhCodesBuildsSearchTextCreatesTagsAndNoteInteractionWithoutUpdatingLastContactedAt()
    {
        var fixedNow = new DateTimeOffset(2026, 11, 25, 8, 30, 0, TimeSpan.Zero);
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(fixedNow));
        });
        await h.LoginAsync();

        var existingTag = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Ấm Tử Sa", color = "#334455" }))
            .Content.ReadFromJsonAsync<CustomerTagView>())!;

        var salesEmail = await CreateStaffAsync(h, "sales");
        Guid salesStaffId;
        await using (var db = h.OwnerContext())
        {
            salesStaffId = await db.Users.Where(u => u.Email == salesEmail).Select(u => u.Id).SingleAsync();
        }

        await h.LoginAsync(salesEmail, StaffNewPassword);

        var csv = BuildCsvBytes(
        [
            FullHeaders,
            ["Đặng Trần Hiên", "0905.123.456", "Hiên Trà", "wholesale", "ấm tử sa; Khách Cũ VIP", "Thích ấm tử nê dung tích 180ml", ""],
            ["Lê Minh Châu", "+84905999888", "", "", "khách cũ vip", "", "false"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "legacy.csv", "text/csv");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

        Assert.True(result.Summary.Committed);
        Assert.Equal(2, result.Summary.ImportedCount);
        Assert.Equal(1, result.Summary.CreatedTagsCount);
        Assert.Equal(["Khách Cũ VIP"], result.Summary.NewTags);
        Assert.Equal(1, result.Summary.CreatedInteractionsCount);

        var firstId = result.Rows[0].CustomerId!.Value;
        var secondId = result.Rows[1].CustomerId!.Value;
        Assert.Equal("KH001000", result.Rows[0].CustomerCode);
        Assert.Equal("KH001001", result.Rows[1].CustomerCode);
        Assert.Equal(["Ấm Tử Sa", "Khách Cũ VIP"], result.Rows[0].Tags);
        Assert.Equal(["Khách Cũ VIP"], result.Rows[1].Tags);

        var detail1 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{firstId}"))!;
        Assert.Equal("legacy_import", detail1.Origin);
        Assert.Equal("legacy", detail1.AcquisitionSource);
        Assert.True(detail1.Stats.HadLegacyPurchase);
        Assert.Null(detail1.Stats.LastContactedAt); // BR-CRM-07
        Assert.Null(detail1.NoteSummary);
        Assert.Equal(2, detail1.Tags.Count);
        Assert.Contains(detail1.Tags, t => t.Id == existingTag.Id);

        var detail2 = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{secondId}"))!;
        Assert.Equal("legacy_import", detail2.Origin);
        Assert.Equal("legacy", detail2.AcquisitionSource);
        Assert.Equal("retail", detail2.CustomerGroup);
        Assert.False(detail2.Stats.HadLegacyPurchase);
        Assert.Null(detail2.Stats.LastContactedAt);

        // BR-CRM-14: unaccent search and last-4-digit phone search
        var searchByName = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>($"{CustomersRoot}?q=dang%20tran%20hien"))!;
        Assert.Contains(searchByName.Items, x => x.Id == firstId);
        var searchByTail = (await h.Client.GetFromJsonAsync<CatalogPage<CustomerSummaryView>>($"{CustomersRoot}?q=3456"))!;
        Assert.Contains(searchByTail.Items, x => x.Id == firstId);

        await using (var db = h.OwnerContext())
        {
            var interactions = await db.Set<Interaction>().AsNoTracking().ToArrayAsync();
            var note = Assert.Single(interactions);
            Assert.Equal(firstId, note.CustomerId);
            Assert.Equal(salesStaffId, note.StaffId);
            Assert.Equal("other", note.Channel);
            Assert.Equal("note", note.Kind);
            Assert.Equal("Thích ấm tử nê dung tích 180ml", note.Content);
            Assert.Equal(fixedNow.UtcDateTime, note.OccurredAt);
            Assert.Equal(fixedNow.UtcDateTime, note.CreatedAt);
            Assert.Null(note.FollowUpAt);
            Assert.Null(note.FollowUpDoneAt);
            Assert.Null(note.FollowUpResult);
        }
    }

    [Fact]
    public async Task O4MixedRowsWithErrorsOnDryRunFalseReturn200CommittedFalseWithZeroSideEffectsAndCleanFileCommits()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var existingTag = (await (await h.MutateAsync(HttpMethod.Post, TagsRoot, new { name = "Nhãn Sẵn Có", color = "#112233" }))
            .Content.ReadFromJsonAsync<CustomerTagView>())!;
        var target = await CreateCustomerAsync(h, new { fullName = "Hồ Sơ Đích", phone = "0906000001" });
        await using (var db = h.OwnerContext())
        {
            var merged = new Customer
            {
                Id = Guid.CreateVersion7(),
                Code = "KH009999",
                FullName = "Hồ Sơ Đã Gộp",
                PhoneE164 = "+84906111222",
                CustomerGroup = "retail",
                Origin = "admin",
                AcquisitionSource = "unknown",
                ContactStatus = "active",
                SearchText = "ho so da gop",
                MergedIntoCustomerId = target.Id,
                CreatedAt = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc)
            };
            db.Add(merged);
            await db.SaveChangesAsync();
        }

        // O4.1=B: dryRun=false with errorCount > 0 returns HTTP 200, committed=false, 0 DB/sequence/audit writes.
        var csvWithErrors = BuildCsvBytes(
        [
            FullHeaders,
            ["Khách Chỉ Zalo", "", "Zalo Trà Đạo", "", "nhãn sẵn có; Nhãn Mới Một", "Liên hệ qua Zalo", ""],
            ["Khách Thiếu Liên Hệ", "", "", "", "Nhãn Của Dòng Lỗi", "", ""],
            ["Khách Số Bàn", "02438223344", "Có Zalo Vẫn Lỗi", "", "", "", ""],
            ["Khách Trùng Hồ Sơ Đã Gộp", "0906111222", "", "", "Nhãn Mới Hai; nhãn mới một", "", ""],
            ["Khách Trùng DB", "0906000001", "", "", "Nhãn Dòng Skip", "Note skip", ""]
        ]);

        var before = await ReadDbSnapshotAsync(h.OwnerConnection);

        using (var blockedResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csvWithErrors, "mixed-errors.csv", "text/csv"))
        {
            var body = await blockedResp.Content.ReadAsStringAsync();
            Assert.True(blockedResp.StatusCode == HttpStatusCode.OK, body);
            var blocked = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

            Assert.False(blocked.Summary.DryRun);
            Assert.False(blocked.Summary.Committed);
            Assert.Equal(5, blocked.Summary.TotalRows);
            Assert.Equal(2, blocked.Summary.ReadyCount);
            Assert.Equal(0, blocked.Summary.ImportedCount);
            Assert.Equal(1, blocked.Summary.SkippedCount);
            Assert.Equal(1, blocked.Summary.ExistingPhoneSkippedCount);
            Assert.Equal(2, blocked.Summary.ErrorCount);
            Assert.Equal(0, blocked.Summary.CreatedTagsCount);
            Assert.Equal(0, blocked.Summary.CreatedInteractionsCount);
            Assert.Equal(["Nhãn Mới Một", "Nhãn Mới Hai"], blocked.Summary.NewTags);

            Assert.Equal("would_import", blocked.Rows[0].Status);
            Assert.Equal("would_import", blocked.Rows[0].ReasonCode);
            Assert.Null(blocked.Rows[0].CustomerId);
            Assert.Null(blocked.Rows[0].CustomerCode);
            Assert.True(blocked.Rows[0].CreateNoteInteraction);
            Assert.Equal([existingTag.Name, "Nhãn Mới Một"], blocked.Rows[0].Tags);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], blocked.Rows[0].Warnings);

            Assert.Equal("error", blocked.Rows[1].Status);
            Assert.Equal("missing_contact", blocked.Rows[1].ReasonCode);

            Assert.Equal("error", blocked.Rows[2].Status);
            Assert.Equal("invalid_phone", blocked.Rows[2].ReasonCode);

            Assert.Equal("would_import", blocked.Rows[3].Status);
            Assert.Equal("would_import", blocked.Rows[3].ReasonCode);
            Assert.Null(blocked.Rows[3].CustomerId);
            Assert.Null(blocked.Rows[3].CustomerCode);
            Assert.Equal("+84906111222", blocked.Rows[3].NormalizedPhone);
            Assert.Equal(["Nhãn Mới Hai", "Nhãn Mới Một"], blocked.Rows[3].Tags);

            Assert.Equal("skipped", blocked.Rows[4].Status);
            Assert.Equal("existing_phone_skipped", blocked.Rows[4].ReasonCode);
            Assert.Equal(target.Id, blocked.Rows[4].ExistingCustomerId);
        }

        // Zero customers, tags, customer_tags, interactions, audit_logs, and zero customer_code_seq consumed.
        Assert.Equal(before, await ReadDbSnapshotAsync(h.OwnerConnection));

        // After fixing the 2 invalid rows, dryRun=false commits cleanly.
        var cleanCsv = BuildCsvBytes(
        [
            FullHeaders,
            ["Khách Chỉ Zalo", "", "Zalo Trà Đạo", "", "nhãn sẵn có; Nhãn Mới Một", "Liên hệ qua Zalo", ""],
            ["Khách Trùng Hồ Sơ Đã Gộp", "0906111222", "", "", "Nhãn Mới Hai; nhãn mới một", "", ""],
            ["Khách Trùng DB", "0906000001", "", "", "Nhãn Dòng Skip", "Note skip", ""]
        ]);

        using (var commitResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", cleanCsv, "mixed-clean.csv", "text/csv"))
        {
            var body = await commitResp.Content.ReadAsStringAsync();
            Assert.True(commitResp.StatusCode == HttpStatusCode.OK, body);
            var committed = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

            Assert.False(committed.Summary.DryRun);
            Assert.True(committed.Summary.Committed);
            Assert.Equal(3, committed.Summary.TotalRows);
            Assert.Equal(2, committed.Summary.ImportedCount);
            Assert.Equal(1, committed.Summary.ExistingPhoneSkippedCount);
            Assert.Equal(0, committed.Summary.ErrorCount);
            Assert.Equal(2, committed.Summary.CreatedTagsCount);
            Assert.Equal(["Nhãn Mới Một", "Nhãn Mới Hai"], committed.Summary.NewTags);
            Assert.Equal(1, committed.Summary.CreatedInteractionsCount);

            Assert.Equal("imported", committed.Rows[0].Status);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], committed.Rows[0].Warnings);
            Assert.Equal("imported", committed.Rows[1].Status);
            Assert.Equal("+84906111222", committed.Rows[1].NormalizedPhone);
            Assert.Equal("existing_phone_skipped", committed.Rows[2].ReasonCode);
        }
    }

    [Fact]
    public async Task O4AllSkippedRowsWithDryRunFalseReturnsCommittedTrueAndConsumesZeroSequence()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var existing = await CreateCustomerAsync(h, new { fullName = "Khách Sẵn Có", phone = "0906888999" });
        var before = await ReadDbSnapshotAsync(h.OwnerConnection);

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Trùng DB", "0906888999", "Tag Không Tạo", "Note không tạo"],
            ["Trùng Trong File", "+84906888999", "Tag Không Tạo 2", "Note không tạo 2"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "all-skipped.csv", "text/csv");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

        Assert.False(result.Summary.DryRun);
        Assert.True(result.Summary.Committed);
        Assert.Equal(2, result.Summary.TotalRows);
        Assert.Equal(0, result.Summary.ReadyCount);
        Assert.Equal(0, result.Summary.ImportedCount);
        Assert.Equal(2, result.Summary.SkippedCount);
        Assert.Equal(1, result.Summary.ExistingPhoneSkippedCount);
        Assert.Equal(1, result.Summary.DuplicateInFileCount);
        Assert.Equal(0, result.Summary.ErrorCount);
        Assert.Equal(0, result.Summary.CreatedTagsCount);
        Assert.Equal(0, result.Summary.CreatedInteractionsCount);
        Assert.Empty(result.Summary.NewTags);

        Assert.Equal(existing.Id, result.Rows[0].ExistingCustomerId);
        Assert.Equal(2, result.Rows[1].DuplicateOfRow);
        Assert.Equal(before, await ReadDbSnapshotAsync(h.OwnerConnection));
    }

    [Fact]
    public async Task O4PossibleDuplicateZaloCustomerWarningDetectsInFileAndDbMatchesAndReimportNeverDuplicatesPhoneInteractionsOrTags()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        // Active customer WITH phone + same zalo_name -> must NOT trigger possible_duplicate_zalo_customer
        var phoneAndZalo = await CreateCustomerAsync(h, new
        {
            fullName = "Khách Có SĐT Và Zalo",
            phone = "0908000001",
            zaloName = "Zalo Có SĐT"
        });

        // Merged Zalo-only customer -> must NOT trigger possible_duplicate_zalo_customer
        await using (var db = h.OwnerContext())
        {
            db.Add(new Customer
            {
                Id = Guid.CreateVersion7(),
                Code = "KH009888",
                FullName = "Khách Zalo Đã Gộp",
                PhoneE164 = null,
                ZaloName = "Zalo Đã Gộp",
                CustomerGroup = "retail",
                Origin = "admin",
                AcquisitionSource = "unknown",
                ContactStatus = "active",
                SearchText = "khach zalo da gop",
                MergedIntoCustomerId = phoneAndZalo.Id,
                CreatedAt = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        var csv = BuildCsvBytes(
        [
            FullHeaders,
            ["Khách Có SĐT", "0908111222", "", "retail", "Tag SĐT", "Ghi chú SĐT duy nhất", ""],
            ["Đặng Trà Hiên", "", "Zalo Trà Đạo", "retail", "Tag Zalo", "Ghi chú Zalo 1", ""],
            ["  đặng trà hiên ", "", "zalo trà đạo", "retail", "", "Ghi chú Zalo 2", ""],
            ["Khách Có SĐT Và Zalo", "", "Zalo Có SĐT", "retail", "", "", ""],
            ["Khách Zalo Đã Gộp", "", "Zalo Đã Gộp", "retail", "", "", ""],
            ["Người Khác Tên", "", "Zalo Trà Đạo", "retail", "", "", ""]
        ]);

        // Round 1 dryRun=true: row 4 matches row 3 in-file; rows 3, 5, 6, 7 have only zalo_only_reimport_risk.
        using (var dry1 = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", csv, "zalo-round1.csv", "text/csv"))
        {
            var body = await dry1.Content.ReadAsStringAsync();
            Assert.True(dry1.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.False(r.Summary.Committed);
            Assert.Equal(["Tag SĐT", "Tag Zalo"], r.Summary.NewTags);
            Assert.Empty(r.Rows[0].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], r.Rows[1].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer], r.Rows[2].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], r.Rows[3].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], r.Rows[4].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], r.Rows[5].Warnings);
        }

        Guid phoneCustomerId;
        // Round 1 dryRun=false: all 6 rows commit (Zalo-only rows are NEVER auto-skipped or auto-merged).
        using (var apply1 = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "zalo-round1.csv", "text/csv"))
        {
            var body = await apply1.Content.ReadAsStringAsync();
            Assert.True(apply1.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.True(r.Summary.Committed);
            Assert.Equal(6, r.Summary.ImportedCount);
            Assert.Equal(2, r.Summary.CreatedTagsCount);
            Assert.Equal(["Tag SĐT", "Tag Zalo"], r.Summary.NewTags);
            Assert.Equal(3, r.Summary.CreatedInteractionsCount);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], r.Rows[1].Warnings);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer], r.Rows[2].Warnings);
            phoneCustomerId = r.Rows[0].CustomerId!.Value;
        }

        // Round 2 re-import of the SAME valid file:
        // - Phone row is skipped with 0 duplicate interactions/tags.
        // - Every Zalo-only row now matches active DB Zalo-only customer case-insensitively -> possible_duplicate_zalo_customer on both dryRun=true & dryRun=false.
        var reimportCsv = BuildCsvBytes(
        [
            FullHeaders,
            ["Khách Có SĐT", "0908111222", "", "retail", "Tag Mới Không Được Gắn", "Ghi chú không được lặp", ""],
            ["ĐẶNG TRÀ HIÊN", "", "ZALO TRÀ ĐẠO", "retail", "", "Ghi chú lần 2", ""]
        ]);

        using (var dry2 = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", reimportCsv, "zalo-round2.csv", "text/csv"))
        {
            var body = await dry2.Content.ReadAsStringAsync();
            Assert.True(dry2.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.Equal("existing_phone_skipped", r.Rows[0].ReasonCode);
            Assert.Equal(phoneCustomerId, r.Rows[0].ExistingCustomerId);
            Assert.False(r.Rows[0].CreateNoteInteraction);
            Assert.Empty(r.Rows[0].Tags);
            Assert.Empty(r.Summary.NewTags);
            Assert.Equal("would_import", r.Rows[1].Status);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer], r.Rows[1].Warnings);
        }

        using (var apply2 = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", reimportCsv, "zalo-round2.csv", "text/csv"))
        {
            var body = await apply2.Content.ReadAsStringAsync();
            Assert.True(apply2.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.True(r.Summary.Committed);
            Assert.Equal(1, r.Summary.ImportedCount);
            Assert.Equal(1, r.Summary.ExistingPhoneSkippedCount);
            Assert.Equal(0, r.Summary.CreatedTagsCount);
            Assert.Empty(r.Summary.NewTags);
            Assert.Equal("imported", r.Rows[1].Status);
            Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer], r.Rows[1].Warnings);
        }

        // Verify skipped phone customer has ZERO duplicate notes or tags created by re-import.
        await using (var db = h.OwnerContext())
        {
            Assert.Equal(1, await db.Set<Interaction>().CountAsync(x => x.CustomerId == phoneCustomerId));
            Assert.Equal(1, await db.Set<CustomerTag>().CountAsync(x => x.CustomerId == phoneCustomerId));
            Assert.False(await db.Set<Tag>().AnyAsync(x => x.Name == "Tag Mới Không Được Gắn"));
        }
    }

    [Fact]
    public async Task O4PossibleDuplicateZaloCustomerWarningMatchesUnicodeWhitespaceU000BU00A0AndVietnameseCaseInBothDryRunAndApply()
    {
        // Guard exact parity between SQL btrim character set and .NET char.IsWhiteSpace (string.Trim()).
        var allDotNetWhitespace = new string(Enumerable.Range(0, 65536).Select(i => (char)i).Where(char.IsWhiteSpace).ToArray());
        Assert.Equal(EfCustomerImportStore.DotNetWhitespaceChars, allDotNetWhitespace);

        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        await using (var db = h.OwnerContext())
        {
            db.Add(new Customer
            {
                Id = Guid.CreateVersion7(),
                Code = "KH009777",
                FullName = "\u000B\u00A0Đặng Trần Hiên\u00A0\u000B",
                PhoneE164 = null,
                ZaloName = "\u00A0Zalo Trà Đạo\u000B\u3000",
                CustomerGroup = "retail",
                Origin = "legacy_import",
                AcquisitionSource = "legacy",
                ContactStatus = "active",
                SearchText = "dang tran hien zalo tra dao",
                MergedIntoCustomerId = null,
                CreatedAt = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        var csv = BuildCsvBytes(
        [
            FullHeaders,
            ["\u00A0đặng trần hiên\u000B", "", "\u000BZALO TRÀ ĐẠO\u00A0", "retail", "", "", ""]
        ]);

        using (var dryResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", csv, "zalo-unicode-ws.csv", "text/csv"))
        {
            var body = await dryResp.Content.ReadAsStringAsync();
            Assert.True(dryResp.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.Equal("would_import", r.Rows[0].Status);
            Assert.Equal(
                [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
                r.Rows[0].Warnings);
        }

        using (var applyResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "zalo-unicode-ws.csv", "text/csv"))
        {
            var body = await applyResp.Content.ReadAsStringAsync();
            Assert.True(applyResp.StatusCode == HttpStatusCode.OK, body);
            var r = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
            Assert.True(r.Summary.Committed);
            Assert.Equal("imported", r.Rows[0].Status);
            Assert.Equal(
                [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
                r.Rows[0].Warnings);
        }
    }

    [Fact]
    public async Task BrCrm03Boundary2000RowsSucceedsAnd2001RowsReturns413WithZeroDbSideEffects()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var rows2001 = new List<string[]>(2002) { ShortHeaders };
        for (var i = 1; i <= 2001; i++) rows2001.Add([$"Khách {i}", $"092{i:D7}"]);

        var before = await ReadDbSnapshotAsync(h.OwnerConnection);
        using (var overResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", BuildCsvBytes(rows2001), "2001.csv", "text/csv"))
        {
            await AssertProblemAsync(overResp, HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded");
        }

        Assert.Equal(before, await ReadDbSnapshotAsync(h.OwnerConnection));

        var rows2000 = rows2001.Take(2001).ToList();
        using var okResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", BuildCsvBytes(rows2000), "2000.csv", "text/csv");
        var body = await okResp.Content.ReadAsStringAsync();
        Assert.True(okResp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
        Assert.Equal(2000, result.Summary.TotalRows);
        Assert.Equal(2000, result.Summary.ImportedCount);
        Assert.Equal(0, result.Summary.ErrorCount);

        await using var db = h.OwnerContext();
        Assert.Equal(2000, await db.Set<Customer>().CountAsync());
    }

    [Fact]
    public async Task ImportConcurrencyTwoParallelImportsWithOverlappingPhonesAndNewTagsSerializeCleanlyWithoutDuplicatesOr500()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var salesEmail = await CreateStaffAsync(h, "sales");
        using var client2 = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await LoginClientAsync(client2, salesEmail, StaffNewPassword);

        var file1 = new List<string[]> { TagNoteHeaders };
        var file2 = new List<string[]> { TagNoteHeaders };
        for (var i = 1; i <= 40; i++)
        {
            file1.Add([$"Batch1 {i}", $"093{i:D7}", "Tag Song Song", $"Note1 {i}"]);
            file2.Add([$"Batch2 {i}", $"093{i:D7}", "tag song song", $"Note2 {i}"]);
        }

        for (var i = 41; i <= 50; i++)
        {
            file1.Add([$"Only1 {i}", $"093{i:D7}", "Tag Song Song; VIP Batch 1", ""]);
            file2.Add([$"Only2 {i}", $"094{i:D7}", "tag song song; VIP Batch 2", ""]);
        }

        var bytes1 = BuildCsvBytes(file1);
        var bytes2 = BuildCsvBytes(file2);

        var t1 = UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", bytes1, "b1.csv", "text/csv");
        var t2 = UploadImportAsync(client2, $"{ImportRoot}?dryRun=false", bytes2, "b2.csv", "text/csv");
        var responses = await Task.WhenAll(t1, t2);
        using var r1 = responses[0];
        using var r2 = responses[1];

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var res1 = (await r1.Content.ReadFromJsonAsync<CustomerImportResult>(WebJson))!;
        var res2 = (await r2.Content.ReadFromJsonAsync<CustomerImportResult>(WebJson))!;

        Assert.Equal(60, res1.Summary.ImportedCount + res2.Summary.ImportedCount);
        Assert.Equal(40, res1.Summary.ExistingPhoneSkippedCount + res2.Summary.ExistingPhoneSkippedCount);

        await using var db = h.OwnerContext();
        Assert.Equal(60, await db.Set<Customer>().CountAsync());
        Assert.Equal(3, await db.Set<Tag>().CountAsync());
    }

    [Fact]
    public async Task ImportRevalidationCustomerCreatedBetweenDryRunAndApplyIsSkippedOnApplyWithoutOverwriting()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var csv = BuildCsvBytes(
        [
            ["full_name", "phone", "note"],
            ["Tên Import", "0907777888", "Ghi chú từ file"],
            ["Khách Mới Hẳn", "0907777999", "Ghi chú khách 2"]
        ]);

        using (var dryResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", csv, "reval.csv", "text/csv"))
        {
            Assert.Equal(HttpStatusCode.OK, dryResp.StatusCode);
            var dry = (await dryResp.Content.ReadFromJsonAsync<CustomerImportResult>(WebJson))!;
            Assert.Equal(2, dry.Summary.ReadyCount);
            Assert.Equal("would_import", dry.Rows[0].Status);
        }

        var createdBetween = await CreateCustomerAsync(h, new { fullName = "Tạo Xen Giữa", phone = "0907777888", noteSummary = "Giữ nguyên" });

        using (var applyResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "reval.csv", "text/csv"))
        {
            Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);
            var applied = (await applyResp.Content.ReadFromJsonAsync<CustomerImportResult>(WebJson))!;
            Assert.Equal(1, applied.Summary.ImportedCount);
            Assert.Equal(1, applied.Summary.ExistingPhoneSkippedCount);
            Assert.Equal("existing_phone_skipped", applied.Rows[0].ReasonCode);
            Assert.Equal(createdBetween.Id, applied.Rows[0].ExistingCustomerId);
            Assert.Equal("imported", applied.Rows[1].Status);
        }

        var reloaded = (await h.Client.GetFromJsonAsync<CustomerDetailView>($"{CustomersRoot}/{createdBetween.Id}"))!;
        Assert.Equal("Tạo Xen Giữa", reloaded.FullName);
        Assert.Equal("Giữ nguyên", reloaded.NoteSummary);
        Assert.Equal(createdBetween.RowVersion, reloaded.RowVersion);
    }

    [Fact]
    public async Task ImportMultipartSecurityRejectsMissingDryRunWrongFieldFormulaMacroOversizedAndMalformedFiles()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var validCsv = BuildCsvBytes([ShortHeaders, ["A", "0901234567"]]);

        using (var r = await UploadImportAsync(h.Client, ImportRoot, validCsv, "a.csv", "text/csv"))
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "invalid_mode");

        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=yes", validCsv, "a.csv", "text/csv"))
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "invalid_mode");

        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", validCsv, "a.csv", "text/csv", fieldName: "upload"))
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "invalid_upload");

        using (var r = await h.MutateAsync(HttpMethod.Post, $"{ImportRoot}?dryRun=true", new { file = "a.csv" }))
            await AssertProblemAsync(r, HttpStatusCode.UnsupportedMediaType, "unsupported_format");

        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", validCsv, "a.txt", "text/plain"))
            await AssertProblemAsync(r, HttpStatusCode.UnsupportedMediaType, "unsupported_format");

        var formulaXlsx = BuildXlsxBytes("KhachHang", [ShortHeaders, ["FORMULA:=1+1", "0901234567"]]);
        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", formulaXlsx, "formula.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"))
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "formula_not_allowed");

        var macroXlsx = BuildXlsxBytes("KhachHang", [ShortHeaders, ["A", "0901234567"]], extraEntries: [("xl/vbaProject.bin", [1, 2, 3])]);
        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", macroXlsx, "macro.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"))
            await AssertProblemAsync(r, HttpStatusCode.UnsupportedMediaType, "unsupported_format");

        var oversized = new byte[(5 * 1024 * 1024) + 1024];
        Array.Fill(oversized, (byte)'a');
        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", oversized, "big.csv", "text/csv"))
            await AssertProblemAsync(r, HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded");

        using (var r = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=true", Encoding.UTF8.GetBytes("not-a-zip"), "broken.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"))
            await AssertProblemAsync(r, HttpStatusCode.BadRequest, "malformed_file");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AcP209ImportSecurityRejectsContentRoleUnauthenticatedUnreadyStaffPlainHttpAndMissingCsrfForBothDryRunModes(string dryRun)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        var contentEmail = await CreateStaffAsync(h, "content", ready: true);
        var unreadySalesEmail = await CreateStaffAsync(h, "sales", ready: false);
        var csv = BuildCsvBytes([ShortHeaders, ["Bảo Mật", "0908888777"]]);
        var path = $"{ImportRoot}?dryRun={dryRun}";

        using (var anon = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        using (var resp = await UploadImportAsync(anon, path, csv, "sec.csv", "text/csv", includeCsrf: false))
            await AssertProblemAsync(resp, HttpStatusCode.Unauthorized, "unauthenticated");

        using (var contentClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(contentClient, contentEmail, StaffNewPassword);
            using var resp = await UploadImportAsync(contentClient, path, csv, "sec.csv", "text/csv");
            await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
        }

        using (var unreadyClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
        {
            await LoginClientAsync(unreadyClient, unreadySalesEmail, StaffInitialPassword);
            using var resp = await UploadImportAsync(unreadyClient, path, csv, "sec.csv", "text/csv");
            await AssertProblemAsync(resp, HttpStatusCode.Forbidden, "forbidden");
        }

        using (var httpClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost"), AllowAutoRedirect = false }))
        using (var resp = await UploadImportAsync(httpClient, path, csv, "sec.csv", "text/csv", includeCsrf: false))
            await AssertProblemAsync(resp, HttpStatusCode.BadRequest, "https_required");

        using (var noCsrf = await UploadImportAsync(h.Client, path, csv, "sec.csv", "text/csv", includeCsrf: false))
            await AssertProblemAsync(noCsrf, HttpStatusCode.BadRequest, "csrf_invalid");

        using var okResp = await UploadImportAsync(h.Client, path, csv, "sec.csv", "text/csv");
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        Assert.True(okResp.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ImportAuditAndLogsRedactAllCustomerTagAndInteractionContentWithZeroRawPiiLeak()
    {
        var capturedLogs = new ConcurrentQueue<string>();
        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogs)));
        });
        await h.LoginAsync();

        const string secretFullName = "Nguyễn Hoàng Bảo Mật";
        const string secretPhone = "0909887766";
        const string secretE164 = "+84909887766";
        const string secretZalo = "ZaloRiengTu999";
        const string secretTag = "NhãnBíMậtHàNội";
        const string secretNote = "Khách quen số bàn 02439998888 tại 99 Trần Hưng Đạo";

        // Step 1: O4.1=B file with invalid phone row -> committed=false, 0 audit rows, 0 raw phone/note leak.
        var csvWithError = BuildCsvBytes(
        [
            FullHeaders,
            [secretFullName, secretPhone, secretZalo, "retail", secretTag, secretNote, "true"],
            ["Tên Lỗi SĐT", "02437776655", "", "", "", "Ghi chú không được lộ", ""]
        ]);

        using (var errResp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csvWithError, "pii-err.csv", "text/csv"))
        {
            var errBody = await errResp.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, errResp.StatusCode);
            var errResult = JsonSerializer.Deserialize<CustomerImportResult>(errBody, WebJson)!;
            Assert.False(errResult.Summary.Committed);
            Assert.Equal(0, errResult.Summary.ImportedCount);
            Assert.DoesNotContain("02437776655", errBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Ghi chú không được lộ", errBody, StringComparison.Ordinal);
        }

        // Step 2: valid file -> committed=true, audited & fully redacted.
        var csv = BuildCsvBytes(
        [
            FullHeaders,
            [secretFullName, secretPhone, secretZalo, "retail", secretTag, secretNote, "true"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "pii.csv", "text/csv");
        var respBody = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var committedResult = JsonSerializer.Deserialize<CustomerImportResult>(respBody, WebJson)!;
        Assert.True(committedResult.Summary.Committed);

        await using var db = h.OwnerContext();
        var auditRows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "crm.customers" || a.EntityType == "crm.tags" || a.EntityType == "crm.interactions")
            .ToArrayAsync();

        Assert.Contains(auditRows, a => a.EntityType == "crm.customers" && a.Action == "create");
        Assert.Contains(auditRows, a => a.EntityType == "crm.tags" && a.Action == "create");
        var interactionAudit = Assert.Single(auditRows, a => a.EntityType == "crm.interactions" && a.Action == "create");
        using (var changesDoc = JsonDocument.Parse(interactionAudit.Changes))
        {
            var root = changesDoc.RootElement;
            Assert.Equal("[redacted]", root.GetProperty("content").GetProperty("after").GetString());
            Assert.Equal("note", root.GetProperty("kind").GetProperty("after").GetString());
            Assert.Equal("other", root.GetProperty("channel").GetProperty("after").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("created_at").GetProperty("after").GetString()));
        }

        var combinedAuditAndLogs = string.Join("\n", auditRows.Select(a => a.Changes).Concat(capturedLogs));
        foreach (var forbidden in new[]
        {
            secretFullName, "nguyen hoang bao mat",
            secretPhone, secretE164,
            secretZalo, "zaloriengtu999",
            secretTag,
            secretNote, "02439998888", "99 Trần Hưng Đạo",
            "02437776655", "Ghi chú không được lộ"
        })
        {
            Assert.DoesNotContain(forbidden, combinedAuditAndLogs, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("02437776655", respBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task P1FixSavepoint23505OnCustomerPhoneWithConcurrentCorrectIdentityShrinkingSnapshotRetriesCleanlyWithZeroOrphanTagsAndZeroDuplicateAudit()
    {
        var attempts = 0;
        Func<int, CancellationToken, Task>? onAttempt = null;
        var hook = new DelegateImportSaveHook((attempt, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return onAttempt is null ? Task.CompletedTask : onAttempt(attempt, ct);
        });

        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddSingleton<ICustomerImportSaveHook>(hook);
        });
        await h.LoginAsync();

        var existing1 = await CreateCustomerAsync(h, new { fullName = "Khách Đổi SĐT", phone = "0901000001" });

        using var racerClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        await LoginClientAsync(racerClient, "owner@example.test", P009Harness.Password);

        Guid? racerCustomerId = null;
        onAttempt = async (attempt, ct) =>
        {
            if (attempt != 1)
            {
                return;
            }

            // 1. Shrink DB snapshot: existing1 releases 0901000001 via real correct-identity endpoint.
            // Without monotonic existingPhones, row 2 would flip to imported and throw KeyNotFoundException (HTTP 500).
            using (var ciResp = await MutateWithClientAsync(
                racerClient,
                HttpMethod.Post,
                $"{CustomersRoot}/{existing1.Id}/correct-identity",
                new { rowVersion = existing1.RowVersion, phone = "0901000099", reason = "Đổi SĐT đúng lúc xảy ra 23505" },
                ct))
            {
                var ciBody = await ciResp.Content.ReadAsStringAsync(ct);
                Assert.True(ciResp.StatusCode == HttpStatusCode.OK, ciBody);
            }

            // 2. Trigger real PG 23505 (ux_customers_phone) on row 3 (0901000002) via real POST admin/customers.
            using (var createResp = await MutateWithClientAsync(
                racerClient,
                HttpMethod.Post,
                CustomersRoot,
                new { fullName = "Khách Đua Thành Công", phone = "0901000002" },
                ct))
            {
                var createBody = await createResp.Content.ReadAsStringAsync(ct);
                Assert.True(createResp.StatusCode == HttpStatusCode.Created, createBody);
                racerCustomerId = JsonSerializer.Deserialize<CustomerDetailView>(createBody, WebJson)!.Id;
            }
        };

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Dòng Vốn Đã Skip", "0901000001", "Tag Dòng Skip", "Note skip"],
            ["Dòng Đụng 23505", "0901000002", "Tag Mồ Côi Không Được Tạo", "Ghi chú không được tạo"],
            ["Dòng Sống Sót", "0901000003", "Tag Sống Sót", "Ghi chú sống sót"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "p1-23505.csv", "text/csv");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.NotNull(racerCustomerId);
        Assert.False(result.Summary.DryRun);
        Assert.True(result.Summary.Committed);
        Assert.Equal(3, result.Summary.TotalRows);
        Assert.Equal(1, result.Summary.ImportedCount);
        Assert.Equal(2, result.Summary.ExistingPhoneSkippedCount);
        Assert.Equal(0, result.Summary.ErrorCount);
        Assert.Equal(1, result.Summary.CreatedTagsCount);
        Assert.Equal(["Tag Sống Sót"], result.Summary.NewTags);
        Assert.Equal(1, result.Summary.CreatedInteractionsCount);

        // Row 2 remains skipped (monotonically non-shrinking existingPhones)
        Assert.Equal("skipped", result.Rows[0].Status);
        Assert.Equal("existing_phone_skipped", result.Rows[0].ReasonCode);
        Assert.Equal(existing1.Id, result.Rows[0].ExistingCustomerId);

        // Row 3 flipped cleanly to existing_phone_skipped after savepoint rollback
        Assert.Equal("skipped", result.Rows[1].Status);
        Assert.Equal("existing_phone_skipped", result.Rows[1].ReasonCode);
        Assert.Equal(racerCustomerId, result.Rows[1].ExistingCustomerId);

        // Row 4 committed cleanly with pre-allocated KH code
        Assert.Equal("imported", result.Rows[2].Status);
        Assert.NotNull(result.Rows[2].CustomerId);
        Assert.StartsWith("KH", result.Rows[2].CustomerCode, StringComparison.Ordinal);
        Assert.Equal(["Tag Sống Sót"], result.Rows[2].Tags);

        var importedId = result.Rows[2].CustomerId!.Value;
        await using var db = h.OwnerContext();
        // Zero orphan tags
        var allTags = await db.Set<Tag>().AsNoTracking().Select(t => t.Name).ToArrayAsync();
        Assert.Equal(["Tag Sống Sót"], allTags);
        // Exactly 1 interaction for surviving row
        var interaction = Assert.Single(await db.Set<Interaction>().AsNoTracking().ToArrayAsync());
        Assert.Equal(importedId, interaction.CustomerId);
        // Zero duplicate AuditLog entries across rolled-back attempt 1 and committed attempt 2
        var customerCreateAudits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "crm.customers" && a.Action == "create")
            .Select(a => a.EntityId)
            .ToArrayAsync();
        Assert.Equal(3, customerCreateAudits.Length); // existing1 + racer + imported row 4
        Assert.Equal(3, customerCreateAudits.Distinct().Count());
        Assert.Contains(importedId, customerCreateAudits);

        var tagCreateAudits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "crm.tags" && a.Action == "create")
            .ToArrayAsync();
        Assert.Single(tagCreateAudits);

        var interactionCreateAudits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "crm.interactions" && a.Action == "create")
            .ToArrayAsync();
        Assert.Single(interactionCreateAudits);
    }

    [Fact]
    public async Task Savepoint23505OnTagNameRaceWithConcurrentTagCreationRetriesCleanlyAndReusesTagWithZeroDuplicateAudit()
    {
        var attempts = 0;
        Func<int, CancellationToken, Task>? onAttempt = null;
        var hook = new DelegateImportSaveHook((attempt, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return onAttempt is null ? Task.CompletedTask : onAttempt(attempt, ct);
        });

        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddSingleton<ICustomerImportSaveHook>(hook);
        });
        await h.LoginAsync();

        using var racerClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        await LoginClientAsync(racerClient, "owner@example.test", P009Harness.Password);

        Guid? racerTagId = null;
        onAttempt = async (attempt, ct) =>
        {
            if (attempt != 1)
            {
                return;
            }

            // Trigger real PG 23505 (ux_tags_name) via real POST admin/tags right before attempt 1 saves.
            using var tagResp = await MutateWithClientAsync(
                racerClient,
                HttpMethod.Post,
                TagsRoot,
                new { name = "Tag Tranh Đua", color = "#224466" },
                ct);
            var tagBody = await tagResp.Content.ReadAsStringAsync(ct);
            Assert.True(tagResp.StatusCode == HttpStatusCode.Created, tagBody);
            racerTagId = JsonSerializer.Deserialize<CustomerTagView>(tagBody, WebJson)!.Id;
        };

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Khách Tag Race 1", "0905100001", "Tag Tranh Đua; Tag Còn Lại", "Ghi chú race 1"],
            ["Khách Tag Race 2", "0905100002", "tag tranh đua", "Ghi chú race 2"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "tag-23505.csv", "text/csv");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.NotNull(racerTagId);
        Assert.True(result.Summary.Committed);
        Assert.Equal(2, result.Summary.ImportedCount);
        Assert.Equal(1, result.Summary.CreatedTagsCount);
        Assert.Equal(["Tag Còn Lại"], result.Summary.NewTags);
        Assert.Equal(2, result.Summary.CreatedInteractionsCount);
        Assert.Equal(["Tag Tranh Đua", "Tag Còn Lại"], result.Rows[0].Tags);
        Assert.Equal(["Tag Tranh Đua"], result.Rows[1].Tags);

        await using var db = h.OwnerContext();
        var dbTags = await db.Set<Tag>().AsNoTracking().OrderBy(t => t.Name).ToArrayAsync();
        Assert.Equal(2, dbTags.Length);
        Assert.Contains(dbTags, t => t.Id == racerTagId && t.Name == "Tag Tranh Đua");
        Assert.Contains(dbTags, t => t.Name == "Tag Còn Lại");

        Assert.Equal(2, await db.Set<Customer>().CountAsync());
        Assert.Equal(2, await db.Set<Interaction>().CountAsync());
        // Zero duplicate AuditLog rows (2 customers, 2 tags total: 1 racer + 1 import, 2 interactions)
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.customers" && a.Action == "create"));
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.tags" && a.Action == "create"));
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.interactions" && a.Action == "create"));
    }

    [Fact]
    public async Task Savepoint23505ExceedingMaxSaveAttemptsReturns409ConcurrencyConflictWithZeroCommittedRowsAndZeroOrphanAudit()
    {
        var attempts = 0;
        Func<int, CancellationToken, Task>? onAttempt = null;
        var hook = new DelegateImportSaveHook((attempt, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return onAttempt is null ? Task.CompletedTask : onAttempt(attempt, ct);
        });

        await using var h = await P009Harness.CreateAsync(fixture, services =>
        {
            services.AddSingleton<ICustomerImportSaveHook>(hook);
        });
        await h.LoginAsync();

        using var racerClient = h.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        await LoginClientAsync(racerClient, "owner@example.test", P009Harness.Password);

        var racingPhones = new[] { "0902000001", "0902000002", "0902000003" };
        onAttempt = async (attempt, ct) =>
        {
            var phone = racingPhones[attempt - 1];
            using var createResp = await MutateWithClientAsync(
                racerClient,
                HttpMethod.Post,
                CustomersRoot,
                new { fullName = $"Khách Đua Lần {attempt}", phone },
                ct);
            var createBody = await createResp.Content.ReadAsStringAsync(ct);
            Assert.True(createResp.StatusCode == HttpStatusCode.Created, createBody);
        };

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Import 1", "0902000001", "Tag Conflict 1", "Note 1"],
            ["Import 2", "0902000002", "Tag Conflict 2", "Note 2"],
            ["Import 3", "0902000003", "Tag Conflict 3", "Note 3"],
            ["Import 4", "0902000004", "Tag Conflict 4", "Note 4"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "exhausted-23505.csv", "text/csv");
        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(3, Volatile.Read(ref attempts));

        await using var db = h.OwnerContext();
        // Only the 3 concurrently created customers exist; row 4 was never committed.
        var dbPhones = await db.Set<Customer>().AsNoTracking().Select(c => c.PhoneE164!).OrderBy(p => p).ToArrayAsync();
        Assert.Equal(["+84902000001", "+84902000002", "+84902000003"], dbPhones);
        // Zero tags, zero customer_tags, zero interactions, zero import audit logs.
        Assert.Equal(0, await db.Set<Tag>().CountAsync());
        Assert.Equal(0, await db.Set<CustomerTag>().CountAsync());
        Assert.Equal(0, await db.Set<Interaction>().CountAsync());
        Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.customers" && a.Action == "create"));
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.tags" && a.Action == "create"));
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.EntityType == "crm.interactions" && a.Action == "create"));
    }

    [Fact]
    public async Task RealTpAppRoleExecutesCustomerImportWithAdvisoryLockSequenceAndInteractionInsert()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        await using (var conn = new NpgsqlConnection(h.RuntimeConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_user", conn);
            Assert.Equal("tp_app", (string)(await cmd.ExecuteScalarAsync())!);
        }

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Khách TpApp", "0904445556", "Tag TpApp", "Ghi chú qua tp_app"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "tpapp.csv", "text/csv");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body);
        var result = JsonSerializer.Deserialize<CustomerImportResult>(body, WebJson)!;
        Assert.Equal(1, result.Summary.ImportedCount);
        Assert.Equal(1, result.Summary.CreatedTagsCount);
        Assert.Equal(1, result.Summary.CreatedInteractionsCount);
    }

    [Fact]
    public async Task ImportTransactionFailureRollsBackCustomersTagsInteractionsAndAuditAtomically()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await h.LoginAsync();

        await using (var conn = new NpgsqlConnection(h.OwnerConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                CREATE OR REPLACE FUNCTION crm.fail_import_interaction_test() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'simulated_import_failure';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER tr_fail_import_interaction_test
                BEFORE INSERT ON crm.interactions
                FOR EACH ROW EXECUTE FUNCTION crm.fail_import_interaction_test();
                """,
                conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var before = await ReadDbSnapshotAsync(h.OwnerConnection);

        var csv = BuildCsvBytes(
        [
            TagNoteHeaders,
            ["Khách Rollback 1", "0903331111", "Tag Rollback", ""],
            ["Khách Rollback 2", "0903332222", "Tag Rollback", "Kích hoạt lỗi trigger"]
        ]);

        using var resp = await UploadImportAsync(h.Client, $"{ImportRoot}?dryRun=false", csv, "rollback.csv", "text/csv");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        await using var db = h.OwnerContext();
        Assert.Equal(0, await db.Set<Customer>().CountAsync());
        Assert.Equal(0, await db.Set<Tag>().CountAsync());
        Assert.Equal(0, await db.Set<CustomerTag>().CountAsync());
        Assert.Equal(0, await db.Set<Interaction>().CountAsync());
        Assert.Equal(before.AuditCount, await db.AuditLogs.CountAsync());
    }

    private static async Task<HttpResponseMessage> UploadImportAsync(
        HttpClient client,
        string path,
        byte[] fileBytes,
        string filename,
        string mediaType,
        bool includeCsrf = true,
        string fieldName = "file")
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        multipart.Add(fileContent, fieldName, filename);

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = multipart };
        if (includeCsrf)
        {
            var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            req.Headers.Add("X-CSRF-TOKEN", csrf);
        }

        return await client.SendAsync(req);
    }

    private static byte[] BuildCsvBytes(IReadOnlyList<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var val = row[i];
                if (val.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                {
                    sb.Append('"').Append(val.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
                else
                {
                    sb.Append(val);
                }
            }

            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildXlsxBytes(
        string sheetName,
        List<string[]> rows,
        (string Path, byte[] Bytes)[]? extraEntries = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                </Types>
                """);

            WriteZipText(zip, "xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{EscapeXml(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);

            WriteZipText(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);

            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            for (var r = 0; r < rows.Count; r++)
            {
                var rowNum = r + 1;
                sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNum}\">");
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var col = ColumnLetter(c + 1);
                    var raw = rows[r][c];
                    if (raw.StartsWith("FORMULA:", StringComparison.Ordinal))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{col}{rowNum}\"><f>{EscapeXml(raw["FORMULA:".Length..])}</f><v>2</v></c>");
                    }
                    else
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{col}{rowNum}\" t=\"inlineStr\"><is><t>{EscapeXml(raw)}</t></is></c>");
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            WriteZipText(zip, "xl/worksheets/sheet1.xml", sb.ToString());

            if (extraEntries is not null)
            {
                foreach (var (path, bytes) in extraEntries)
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                    using var es = entry.Open();
                    es.Write(bytes);
                }
            }
        }

        return ms.ToArray();
    }

    private static void WriteZipText(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ColumnLetter(int col)
    {
        var sb = new StringBuilder();
        while (col > 0)
        {
            col--;
            sb.Insert(0, (char)('A' + (col % 26)));
            col /= 26;
        }

        return sb.ToString();
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed record DbSnapshot(long Customers, long Tags, long CustomerTags, long Interactions, long AuditCount, long SeqLastValue, bool SeqIsCalled);

    private static async Task<DbSnapshot> ReadDbSnapshotAsync(string ownerConnection)
    {
        await using var conn = new NpgsqlConnection(ownerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM crm.customers),
                (SELECT count(*) FROM crm.tags),
                (SELECT count(*) FROM crm.customer_tags),
                (SELECT count(*) FROM crm.interactions),
                (SELECT count(*) FROM platform.audit_logs),
                (SELECT last_value FROM crm.customer_code_seq),
                (SELECT is_called FROM crm.customer_code_seq)
            """,
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DbSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetBoolean(6));
    }

    private static async Task<HttpResponseMessage> MutateWithClientAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        CancellationToken ct)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf", ct))!.Token;
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(req, ct);
    }

    private sealed class DelegateImportSaveHook(Func<int, CancellationToken, Task> onBeforeSave) : ICustomerImportSaveHook
    {
        public Task BeforeSaveAttemptAsync(int attempt, CancellationToken ct) => onBeforeSave(attempt, ct);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Enqueue($"[{category}] {formatter(state, exception)} {exception?.Message}");
        }
    }
}
