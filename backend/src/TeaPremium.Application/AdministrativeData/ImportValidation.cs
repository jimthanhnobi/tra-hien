using System.Text.RegularExpressions;
using TeaPremium.Application.Settings;

namespace TeaPremium.Application.AdministrativeData;

public static partial class ImportValidation
{
    public const string PinnedCommit = "62b1abdfe39625289c37a58ef6678e9db3d37f84";
    public const string Repository = "thanglequoc/vietnamese-provinces-database";
    public const string PinnedCsvSha256 = "c023b11aab28f64c652d26512a020f06a0e62005d0f64eeee5543362fb85ab4c";
    public static readonly IReadOnlyDictionary<string, string> PinnedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["json/full_json_generated_data_vn_units.json"] = "965c9a2ad85481e18ed2464fd87666eda311856b86a72a87f1130c27376b8d2f",
        ["json/vn_provinces_metadata.json"] = "14f7815413702837918e9aa0961308b072e0d75b2bdf5b5865fc4a23cdd8eda4",
        ["README.md"] = "f6ad9fe458b4fd486a7c5e6b00e3be0c7f36bb9aed5ed77d3e2ad2cfef6ae3dc",
        ["LICENSE"] = "0173d3551f805ee7fabad1948b1219e79dca42f5f93a1b07d4f9a12f9cbfb8da"
    };

    public static AdministrativePayload Validate(ImportBundle bundle, DateOnly today, bool production)
    {
        var m = bundle.Manifest();
        Require(m.SchemaVersion == "vn-admin-manifest/1" && m.DatasetId == "vn-administrative-units"
            && !string.IsNullOrWhiteSpace(m.DatasetVersion) && m.ReleaseOrder > 0 && m.EffectiveOn <= today
            && m.Mode is "full" or "delta" && m.Operation is "import" or "recovery" or "re_advance"
            && m.Normalization == "vn-admin-csv/1", "invalid_manifest_identity");
        Require(m.RetrievedAt != default && m.VerifiedAt >= m.RetrievedAt, "invalid_manifest_dates");
        Require(m.Csv is not null && m.Expected is not null && m.CommunitySource is not null
            && m.OfficialSource is not null && m.Deactivate is not null, "incomplete_manifest");
        Require(m.Csv!.Bytes == bundle.CsvBytes.LongLength && Hash(m.Csv.Sha256)
            && SettingsJson.Hash(bundle.CsvBytes) == m.Csv.Sha256 && m.Csv.Encoding == "utf-8"
            && m.Csv.Delimiter == "," && m.Csv.Newline is "LF" or "CRLF", "csv_checksum_mismatch");
        Require(bundle.CsvBytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) == m.Csv.Bom, "csv_bom_mismatch");
        var source = m.CommunitySource!;
        Require(source.Repository == Repository && Regex.IsMatch(source.Commit ?? "", "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
            && !string.IsNullOrWhiteSpace(source.Tag) && source.License == "MIT" && Hash(source.LicenseSha256)
            && source.Files is { Length: > 0 and <= 32 }, "invalid_source_identity");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in source.Files!)
        {
            Require(seen.Add(file.Path) && Hash(file.Sha256)
                && file.Url == $"https://raw.githubusercontent.com/{Repository}/{source.Commit}/{file.Path}"
                && bundle.SourceFiles.TryGetValue(file.Path, out var actual) && actual.LongLength == file.Bytes
                && SettingsJson.Hash(actual) == file.Sha256, "source_checksum_mismatch");
        }
        Require(bundle.SourceFiles.TryGetValue("LICENSE", out var license) && SettingsJson.Hash(license) == source.LicenseSha256, "missing_license");
        if (source.Commit == PinnedCommit)
        {
            Require(source.Tag == "v5.2.0" && m.DatasetVersion == "v5.2.0" && m.EffectiveOn == new DateOnly(2026, 9, 20), "pinned_release_mismatch");
            foreach (var file in PinnedFiles)
                Require(bundle.SourceFiles.TryGetValue(file.Key, out var bytes) && SettingsJson.Hash(bytes) == file.Value, "pinned_source_mismatch");
            Require(m.Csv.Sha256 == PinnedCsvSha256, "pinned_csv_mismatch");
        }
        // Counts are per-manifest; only the pinned fixture has the specific release assertion.
        var payload = AdministrativeCsv.Parse(bundle.CsvBytes);
        Require(m.Expected!.Provinces == payload.Catalog.Provinces.Length && m.Expected.Wards == payload.Catalog.Wards.Length
            && m.Expected.ProvinceOnlyRows == payload.ProvinceOnlyRows && m.Expected.DataRows == payload.DataRows, "count_mismatch");
        if (source.Commit == PinnedCommit) Require(payload.Catalog.Provinces.Length == 34 && payload.Catalog.Wards.Length == 3321 && payload.DataRows == 3355, "pinned_count_mismatch");
        Require(m.Deactivate!.Provinces is not null && m.Deactivate.Wards is not null
            && m.Deactivate.Provinces.Distinct(StringComparer.Ordinal).Count() == m.Deactivate.Provinces.Length
            && m.Deactivate.Wards.Distinct(StringComparer.Ordinal).Count() == m.Deactivate.Wards.Length, "invalid_deactivations");
        Require(!payload.Catalog.Provinces.Any(p => m.Deactivate.Provinces.Contains(p.Code, StringComparer.Ordinal))
            && !payload.Catalog.Wards.Any(w => m.Deactivate.Wards.Contains(w.Code, StringComparer.Ordinal)), "deactivate_upsert_conflict");
        Require(m.Mode != "full" || !string.IsNullOrWhiteSpace(m.OfficialSource!.CompletenessEvidence), "missing_completeness");
        if (production)
            Require(m.OfficialSource!.VerificationRecord != "NOT_VERIFIED_LOCAL_FIXTURE" && !string.IsNullOrWhiteSpace(m.OfficialSource.Issuer)
                && Uri.TryCreate(m.OfficialSource.Url, UriKind.Absolute, out _) && m.OfficialSource.Artifacts is { Length: > 0 }
                && m.OfficialSource.LegalReferences is { Length: > 0 } && !string.IsNullOrWhiteSpace(m.OfficialSource.VerifiedBy)
                && m.OfficialSource.AsOf == m.EffectiveOn && m.OfficialSource.VerifiedAt is not null
                && m.OfficialSource.Artifacts.All(a => Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && Hash(a.Sha256) && a.RetrievedAt != default)
                && m.OfficialSource.LegalReferences.All(l => !string.IsNullOrWhiteSpace(l.Number) && l.IssuedOn <= l.EffectiveOn
                    && l.EffectiveOn <= m.EffectiveOn && Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"), "production_evidence_missing");
        return payload;
    }
    public static void Require(bool valid, string code) { if (!valid) throw new ImportRejectedException(code); }
    public static bool Hash(string? value) => value is not null && HashPattern().IsMatch(value);
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
}

public sealed class ImportRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
