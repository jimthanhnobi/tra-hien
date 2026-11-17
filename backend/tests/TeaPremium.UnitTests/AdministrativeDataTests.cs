using System.Text;
using System.Text.Json;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Settings;

namespace TeaPremium.UnitTests;

public sealed class AdministrativeDataTests
{
    private const string Header = "province_code,province_name,ward_code,ward_name\n";
    [Fact]
    public void UnicodeQuotedFieldsBomAndProvinceOnlyPreserveCodes()
    {
        var input = Encoding.UTF8.GetBytes("\uFEFF" + Header + "01,Thành phố Hà Nội,,\n01,Thành phố Hà Nội,00004,\"Phường Ba, Đình\"\n02,Tỉnh khác,,\n");
        var result = AdministrativeCsv.Parse(input);
        Assert.Equal(2, result.Catalog.Provinces.Length);
        Assert.Equal("00004", Assert.Single(result.Catalog.Wards).Code);
        Assert.Equal("Phường Ba, Đình", result.Catalog.Wards[0].Name);
        Assert.Equal(3, result.DataRows);
    }
    [Theory]
    [InlineData("01,Tỉnh,,\n01,Tỉnh,,\n")]
    [InlineData("1,Tỉnh,,\n")]
    [InlineData("01,Tỉnh,00001,Xã\n")]
    [InlineData("01,Tỉnh,,\n01,Khác,00001,Xã\n")]
    [InlineData("01,Tỉnh,,\n01,Tỉnh,00001,\n")]
    [InlineData("01,Tỉnh,,\n01,Tỉnh,00001,Xã\n01,Tỉnh,00001,Xã\n")]
    [InlineData("01,\"Tỉnh,,\n")]
    [InlineData("01,\"Tỉnh\"x,,\n")]
    [InlineData("01,Tỉnh,,,\n")]
    public void InvalidCsvIsRejected(string rows) => Assert.True(Assert.Throws<AdministrativeCsvException>(() => AdministrativeCsv.Parse(Encoding.UTF8.GetBytes(Header + rows))).Line >= 2);

    [Fact]
    public void InvalidUtf8AndDuplicateManifestKeysAreRejected()
    {
        Assert.Throws<DecoderFallbackException>(() => AdministrativeCsv.Parse([0xff, 0xfe]));
        Assert.Throws<FormatException>(() => ImportJson.Read<ImportManifest>(Encoding.UTF8.GetBytes("{\"dataset_id\":1,\"dataset_id\":2}")));
    }
    [Fact]
    public void FullDeltaAndHistoryArePreserved()
    {
        var a = Bundle(1, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã cũ\n");
        var initial = new ImportState(new([], []), [], "refs", 0);
        var first = Plan(a, initial);
        var e1 = Event(a, first, null);
        var b = Bundle(2, Header + "01,Tỉnh mới,,\n", e1.Id);
        var second = Plan(b, new(first.Catalog, [e1], "refs", 0));
        Assert.False(Assert.Single(second.Catalog.Wards).IsActive);
        Assert.Contains(second.Plan.Changes, c => c.Code == "00001" && c.Action == "deactivate");
        var delta = b with { ManifestBytes = ImportJson.Write(b.Manifest() with { Mode = "delta" }) };
        Assert.True(Assert.Single(Plan(delta, new(first.Catalog, [e1], "refs", 0)).Catalog.Wards).IsActive);
    }

    [Fact]
    public void ApprovedRecoveryReadvanceKeepsHighWaterAndRejectsStaleOrDifferentPayload()
    {
        var a = Bundle(1, Header + "01,Tỉnh A,,\n");
        var p1 = Plan(a, new(new([], []), [], "refs", 0)); var e1 = Event(a, p1, null);
        var b = Bundle(2, Header + "01,Tỉnh B,,\n", e1.Id);
        var p2 = Plan(b, new(p1.Catalog, [e1], "refs", 0)); var e2 = Event(b, p2, e1.Id);
        var recovery = a with
        {
            ManifestBytes = ImportJson.Write(a.Manifest() with
            {
                Operation = "recovery",
                BaseImportId = e2.Id,
                RecoveryOf = e2.Id,
                TargetImportId = e1.Id,
                Reason = "source correction",
                IncidentRecord = "incident:test",
                ReAdvancePath = [e2.Id]
            })
        };
        var p3 = Plan(recovery, new(p2.Catalog, [e1, e2], "refs", 0)); var e3 = Event(recovery, p3, e2.Id);
        Assert.Equal(2, p3.Plan.HistoricalHighWater);
        var forward = b with
        {
            ManifestBytes = ImportJson.Write(b.Manifest() with
            {
                Operation = "re_advance",
                BaseImportId = e3.Id,
                ReAdvanceOf = e2.Id,
                RecoveryEventId = e3.Id,
                PathPosition = 0
            })
        };
        var state = new ImportState(p3.Catalog, [e1, e2, e3], "refs", 0);
        var p4 = Plan(forward, state);
        Assert.Equal(2, p4.Plan.HistoricalHighWater);
        Assert.Equal("Tỉnh B", Assert.Single(p4.Catalog.Provinces).Name);
        var changed = forward with { ManifestBytes = ImportJson.Write(forward.Manifest() with { Csv = forward.Manifest().Csv with { Sha256 = new string('a', 64) } }) };
        Assert.Equal("release_payload_changed", Assert.Throws<ImportRejectedException>(() => Plan(changed, state)).Code);
        var stale = forward with { ManifestBytes = ImportJson.Write(forward.Manifest() with { BaseImportId = e2.Id }) };
        Assert.Equal("stale_plan", Assert.Throws<ImportRejectedException>(() => Plan(stale, state)).Code);
        Assert.Throws<ImportRejectedException>(() => Plan(b, state)); // Historical B is not an automatic NO-OP.
        Assert.Throws<ImportRejectedException>(() => Plan(a, new(p2.Catalog, [e1, e2], "refs", 0))); // No unapproved downgrade.
    }
    private static PlannedCatalog Plan(ImportBundle b, ImportState s) => ImportPlanner.Create(b, AdministrativeCsv.Parse(b.CsvBytes), s, "test", "LocalTest", []);

    [Fact]
    public void DeltaRecoveryIsRejectedButApprovedDeltaReadvanceHasItsOriginalBase()
    {
        var a = Bundle(1, Header + "01,Tỉnh A,,\n"); var pa = Plan(a, new(new([], []), [], "refs", 0)); var ea = Event(a, pa, null);
        var b = Bundle(2, Header + "01,Tỉnh B,,\n", ea.Id); b = b with { ManifestBytes = ImportJson.Write(b.Manifest() with { Mode = "delta" }) };
        var pb = Plan(b, new(pa.Catalog, [ea], "refs", 0)); var eb = Event(b, pb, ea.Id);
        var recovery = a with
        {
            ManifestBytes = ImportJson.Write(a.Manifest() with
            {
                BaseImportId = eb.Id,
                Operation = "recovery",
                RecoveryOf = eb.Id,
                TargetImportId = ea.Id,
                Reason = "correction",
                IncidentRecord = "incident:test",
                ReAdvancePath = [eb.Id]
            })
        };
        var pr = Plan(recovery, new(pb.Catalog, [ea, eb], "refs", 0)); var er = Event(recovery, pr, eb.Id);
        var forward = b with { ManifestBytes = ImportJson.Write(b.Manifest() with { BaseImportId = er.Id, Operation = "re_advance", ReAdvanceOf = eb.Id, RecoveryEventId = er.Id, PathPosition = 0 }) };
        Assert.Equal("Tỉnh B", Assert.Single(Plan(forward, new(pr.Catalog, [ea, eb, er], "refs", 0)).Catalog.Provinces).Name);
        var invalidRecovery = b with
        {
            ManifestBytes = ImportJson.Write(b.Manifest() with
            {
                BaseImportId = er.Id,
                Operation = "recovery",
                RecoveryOf = er.Id,
                TargetImportId = eb.Id,
                Reason = "correction",
                IncidentRecord = "incident:test",
                ReAdvancePath = []
            })
        };
        Assert.Equal("recovery_requires_full_snapshot", Assert.Throws<ImportRejectedException>(() => Plan(invalidRecovery, new(pr.Catalog, [ea, eb, er], "refs", 0))).Code);
    }
    private static ImportEvent Event(ImportBundle b, PlannedCatalog p, Guid? previous)
        => new(Guid.NewGuid(), previous, b.Manifest(), SettingsJson.Hash(b.ManifestBytes), SettingsJson.Hash(b.CsvBytes), p.Plan.Fingerprint(), p.Catalog.Digest(), "{}", default);
    public static ImportBundle Bundle(long ordinal, string csv, Guid? previous = null)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var payload = AdministrativeCsv.Parse(bytes);
        var source = new Dictionary<string, byte[]> { ["LICENSE"] = Encoding.UTF8.GetBytes("MIT synthetic fixture") };
        var m = new ImportManifest("vn-admin-manifest/1", "vn-administrative-units", "test-" + ordinal, ordinal,
            new DateOnly(2026, 1, (int)ordinal), "full", "import", "vn-admin-csv/1",
            new(ImportValidation.Repository, "test-" + ordinal, new string('a', 40),
                [new("LICENSE", $"https://raw.githubusercontent.com/{ImportValidation.Repository}/{new string('a', 40)}/LICENSE", source["LICENSE"].Length, SettingsJson.Hash(source["LICENSE"]))],
                "MIT", SettingsJson.Hash(source["LICENSE"])),
            new(null, null, null, [], [], "NOT_VERIFIED_LOCAL_FIXTURE", null, null, "complete synthetic fixture"),
            new("provinces.csv", bytes.Length, SettingsJson.Hash(bytes), "utf-8", ",", "LF", false),
            new(payload.Catalog.Provinces.Length, payload.Catalog.Wards.Length, payload.ProvinceOnlyRows, payload.DataRows),
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), previous, new([], []), "synthetic change evidence");
        return new(bytes, ImportJson.Write(m), source);
    }
}
