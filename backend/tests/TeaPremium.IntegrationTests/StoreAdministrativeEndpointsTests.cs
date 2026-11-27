using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeaPremium.Application.AdministrativeData;
using static TeaPremium.IntegrationTests.CustomersApiTests;

namespace TeaPremium.IntegrationTests;

public sealed class StoreAdministrativeEndpointsTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string CsvHeader = "province_code,province_name,ward_code,ward_name\n";

    [Fact]
    public async Task ProvincesAndWardsReturn422WhenNotReadyAnd400OnInvalidProvinceCodeFirst()
    {
        await using var h = await P009Harness.CreateAsync(fixture);

        // Anonymous GET /api/v1/store/provinces before administrative import -> 422 administrative_data_not_ready
        using var provincesResp = await h.Client.GetAsync("/api/v1/store/provinces");
        await AssertProblemAsync(provincesResp, HttpStatusCode.UnprocessableEntity, "administrative_data_not_ready");

        // Anonymous GET /api/v1/store/provinces/79/wards before import -> 422 administrative_data_not_ready
        using var wardsResp = await h.Client.GetAsync("/api/v1/store/provinces/79/wards");
        await AssertProblemAsync(wardsResp, HttpStatusCode.UnprocessableEntity, "administrative_data_not_ready");

        // Invalid {code} must fail validation (400 validation_failed) BEFORE readiness check
        // (NUL bytes are rejected even earlier at HTTP transport level and directly covered in UnitTests.)
        foreach (var badCode in new[] { "01234567890123456", "79!@$", "79%23", "79;DROP", "%20%20%20", "79%0A" })
        {
            using var badResp = await h.Client.GetAsync($"/api/v1/store/provinces/{badCode}/wards");
            await AssertProblemAsync(badResp, HttpStatusCode.BadRequest, "validation_failed");
        }
    }

    [Fact]
    public async Task ProvincesAndWardsReturnOnlyActiveSortedItemsAnd404ForMissingOrInactiveProvince()
    {
        await using var h = await P009Harness.CreateAsync(fixture);

        // Import initial full dataset with 3 provinces and multiple wards (ASCII-leading names for locale-independent Name ASC + Code ASC tie-break)
        var csvV1 = CsvHeader
            + "79,TP Hồ Chí Minh,,\n"
            + "79,TP Hồ Chí Minh,79002,Phường Bến Thành\n"
            + "79,TP Hồ Chí Minh,79001,Phường An Phú\n"
            + "79,TP Hồ Chí Minh,79003,Phường Cát Lái\n"
            + "01,Hà Nội,,\n"
            + "01,Hà Nội,00002,Phường Trúc Bạch\n"
            + "01,Hà Nội,00001,Phường Ba Đình\n"
            + "48,Bình Định,,\n"
            + "48,Bình Định,48001,Phường Quy Nhơn\n";

        var bundleV1 = P009ImportTests.Bundle(1, csvV1);
        var event1 = await ApplyAsync(h, bundleV1);

        // Anonymous access without login or CSRF
        using var listResp1 = await h.Client.GetAsync("/api/v1/store/provinces");
        Assert.Equal(HttpStatusCode.OK, listResp1.StatusCode);
        var list1 = (await listResp1.Content.ReadFromJsonAsync<StoreProvinceListView>())!;
        Assert.Equal(3, list1.Items.Count);
        // All have sortOrder=0 -> ordered by SortOrder ASC, Name ASC, Code ASC: Bình Định (48), Hà Nội (01), TP Hồ Chí Minh (79)
        Assert.Equal(["48", "01", "79"], list1.Items.Select(x => x.Code).ToArray());

        // Wards of province 79 -> ordered by Name ASC, Code ASC
        using var hcmWardsResp = await h.Client.GetAsync("/api/v1/store/provinces/79/wards");
        Assert.Equal(HttpStatusCode.OK, hcmWardsResp.StatusCode);
        var hcmWards = (await hcmWardsResp.Content.ReadFromJsonAsync<StoreWardListView>())!;
        Assert.Equal("79", hcmWards.ProvinceCode);
        Assert.Equal("TP Hồ Chí Minh", hcmWards.ProvinceName);
        Assert.Equal(["79001", "79002", "79003"], hcmWards.Items.Select(x => x.Code).ToArray());
        Assert.All(hcmWards.Items, w => Assert.Equal("79", w.ProvinceCode));

        // Trimmed code with leading/trailing spaces works
        using var trimmedResp = await h.Client.GetAsync("/api/v1/store/provinces/%2001%20/wards");
        Assert.Equal(HttpStatusCode.OK, trimmedResp.StatusCode);
        var hnWards = (await trimmedResp.Content.ReadFromJsonAsync<StoreWardListView>())!;
        Assert.Equal("01", hnWards.ProvinceCode);
        Assert.Equal(["00001", "00002"], hnWards.Items.Select(x => x.Code).ToArray());

        // Unknown province -> 404 not_found
        using var missingResp = await h.Client.GetAsync("/api/v1/store/provinces/99/wards");
        await AssertProblemAsync(missingResp, HttpStatusCode.NotFound, "not_found");

        // Now import full v2 removing province 48 (Đà Nẵng) and ward 79003 (Phường Cát Lái) -> both become is_active = false
        var csvV2 = CsvHeader
            + "79,TP Hồ Chí Minh,,\n"
            + "79,TP Hồ Chí Minh,79002,Phường Bến Thành\n"
            + "79,TP Hồ Chí Minh,79001,Phường An Phú\n"
            + "01,Hà Nội,,\n"
            + "01,Hà Nội,00001,Phường Ba Đình\n";
        var bundleV2 = P009ImportTests.Bundle(2, csvV2, previous: event1, mode: "full");
        await ApplyAsync(h, bundleV2);

        // Inactive province 48 excluded from list and returns 404 on wards
        using var listResp2 = await h.Client.GetAsync("/api/v1/store/provinces");
        Assert.Equal(HttpStatusCode.OK, listResp2.StatusCode);
        var list2 = (await listResp2.Content.ReadFromJsonAsync<StoreProvinceListView>())!;
        Assert.Equal(2, list2.Items.Count);
        Assert.DoesNotContain(list2.Items, x => x.Code == "48");

        using var inactiveProvWards = await h.Client.GetAsync("/api/v1/store/provinces/48/wards");
        await AssertProblemAsync(inactiveProvWards, HttpStatusCode.NotFound, "not_found");

        // Inactive ward 79003 excluded from province 79 wards
        using var hcmWards2Resp = await h.Client.GetAsync("/api/v1/store/provinces/79/wards");
        Assert.Equal(HttpStatusCode.OK, hcmWards2Resp.StatusCode);
        var hcmWards2 = (await hcmWards2Resp.Content.ReadFromJsonAsync<StoreWardListView>())!;
        Assert.Equal(["79001", "79002"], hcmWards2.Items.Select(x => x.Code).ToArray());

        // Verify zero PII or internal fields in raw JSON responses
        var rawProvincesJson = await listResp2.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rawProvincesJson);
        Assert.Single(doc.RootElement.EnumerateObject()); // only "items"
    }

    private static async Task<Guid> ApplyAsync(P009Harness h, ImportBundle bundle)
    {
        var plan = await h.PlanAsync(bundle);
        var result = await h.ApplyAsync(bundle, h.Approve(bundle, plan));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        return result.Value.EventId!.Value;
    }
}
