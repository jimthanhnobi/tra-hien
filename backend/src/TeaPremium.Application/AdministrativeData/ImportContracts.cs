using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Application.Settings;

namespace TeaPremium.Application.AdministrativeData;

public sealed record SourceFile(string Path, string Url, long Bytes, string Sha256);
public sealed record CommunitySource(string Repository, string Tag, string Commit, SourceFile[] Files, string License, string LicenseSha256);
public sealed record OfficialArtifact(string Url, DateTimeOffset RetrievedAt, string Sha256);
public sealed record LegalReference(string Number, DateOnly IssuedOn, DateOnly EffectiveOn, string Url);
public sealed record OfficialSource(string? Issuer, string? Url, DateOnly? AsOf, OfficialArtifact[] Artifacts,
    LegalReference[] LegalReferences, string VerificationRecord, string? VerifiedBy, DateTimeOffset? VerifiedAt, string? CompletenessEvidence);
public sealed record CsvDescriptor(string File, long Bytes, string Sha256, string Encoding, string Delimiter, string Newline, bool Bom);
public sealed record ExpectedCounts(int Provinces, int Wards, int ProvinceOnlyRows, int DataRows);
public sealed record Deactivations(string[] Provinces, string[] Wards);
public sealed record ImportManifest(string SchemaVersion, string DatasetId, string DatasetVersion, long ReleaseOrder,
    DateOnly EffectiveOn, string Mode, string Operation, string Normalization, CommunitySource CommunitySource,
    OfficialSource OfficialSource, CsvDescriptor Csv, ExpectedCounts Expected, DateTimeOffset RetrievedAt,
    DateTimeOffset VerifiedAt, Guid? BaseImportId, Deactivations Deactivate, string? ChangeEvidence,
    Guid? RecoveryOf = null, string? Reason = null, string? IncidentRecord = null, Guid? TargetImportId = null,
    Guid[]? ReAdvancePath = null, Guid? ReAdvanceOf = null, Guid? RecoveryEventId = null, int? PathPosition = null)
{
    public string Identity() => SettingsJson.Serialize(new object[] { DatasetId, CommunitySource.Repository, DatasetVersion });
    public string PayloadIdentity() => SettingsJson.Serialize(new object[] { CommunitySource.Commit,
        CommunitySource.Files.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => new object[] { x.Path, x.Sha256 }).ToArray(),
        Csv.Sha256, Csv.Bytes, Normalization, Mode, Deactivate, ReleaseOrder, EffectiveOn });
}
public sealed record ImportBundle(byte[] CsvBytes, byte[] ManifestBytes, IReadOnlyDictionary<string, byte[]> SourceFiles)
{
    public ImportManifest Manifest() => ImportJson.Read<ImportManifest>(ManifestBytes);
}
public sealed record ImportEvent(Guid Id, Guid? PreviousImportId, ImportManifest Manifest, string ManifestSha256,
    string CsvSha256, string PlanSha256, string CatalogSha256, string Receipt, DateTime AppliedAt);
public sealed record ImportState(AdministrativeCatalog Catalog, ImportEvent[] Events, string ReferenceDigest, int ReferenceCount);
public sealed record CatalogChange(string Kind, string Code, string Action, string? Before, string? After);
public sealed record ImportPlan(string SchemaVersion, string Profile, string Target, string[] Migrations, Guid? BaseImportId,
    string ManifestSha256, string CsvSha256, string ImporterVersion, string BeforeDigest, string AfterDigest,
    string ReferenceDigest, int ReferenceCount, long HistoricalHighWater, DateOnly? HighWaterEffectiveOn,
    long? ActiveReleaseOrder, CatalogChange[] Changes, int Provinces, int Wards, string[] Errors,
    Guid? RecoveryEventId = null, Guid? ReAdvanceOf = null, int? PathPosition = null)
{
    public byte[] Bytes() => ImportJson.Write(this);
    public string Fingerprint() => SettingsJson.Hash(Bytes());
}
public sealed record ImportApproval(string PlanSha256, string Profile, string Target, Guid OperatorId,
    DateTimeOffset ApprovedAt, string[] DeactivateProvinces, string[] DeactivateWards,
    string[] ReparentWards, string[] ReactivateCodes, string? BackupReference, string? BackupSha256,
    Guid? RecoveryEventId, Guid? ReAdvanceOf, int? PathPosition);
public sealed record ImportExecution(ImportPlan Plan, ImportApproval? Approval, string? ApprovalSha256, Guid? OperatorId, bool DryRun,
    string? OperatorStamp = null);
public sealed record ImportOutcome(string Status, ImportPlan Plan, Guid? EventId);

public static class ImportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static T Read<T>(byte[] bytes)
    {
        if (bytes.Length > AdministrativeCsv.MaxBytes) throw new FormatException("import_input_too_large");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        using var doc = JsonDocument.Parse(text);
        if (SettingsJson.HasDuplicateProperties(doc.RootElement)) throw new FormatException("duplicate_manifest_property");
        return JsonSerializer.Deserialize<T>(text, Options) ?? throw new FormatException("invalid_import_json");
    }
    // Record property order is the versioned fixed order; never use unordered dictionaries in a plan.
    public static byte[] Write<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options) + "\n");
}
