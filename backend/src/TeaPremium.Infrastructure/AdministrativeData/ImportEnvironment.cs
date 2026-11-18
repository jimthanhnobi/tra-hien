using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Settings;

namespace TeaPremium.Infrastructure.AdministrativeData;

// Trusted configuration comes from deployment/OS ACLs, never CLI arguments or manifest fields.
public sealed class ImportEnvironment(IConfiguration configuration, IHostEnvironment environment)
{
    public string Profile => environment.IsDevelopment() || environment.IsEnvironment("Test") ? "LocalTest" : "Production";
    public string Target(string connectionString)
    {
        var db = new NpgsqlConnectionStringBuilder(connectionString);
        var identity = SettingsJson.Serialize(new object[] { db.Host ?? "", db.Port, db.Database ?? "", environment.EnvironmentName });
        if (Profile == "LocalTest")
            ImportValidation.Require((db.Database?.StartsWith("teapremium_dev", StringComparison.Ordinal) == true
                || db.Database?.StartsWith("teapremium_test", StringComparison.Ordinal) == true)
                && (db.Host is "localhost" or "127.0.0.1" or "::1" or "host.docker.internal")
                && configuration.GetSection("AdministrativeImport:AllowedTargets").Get<string[]>()?.Contains(identity, StringComparer.Ordinal) == true,
                "unsafe_import_target");
        else ImportValidation.Require(configuration["AdministrativeImport:ProductionTarget"] == identity, "production_import_hold");
        return identity;
    }
    public void VerifyManifest(ImportBundle bundle)
    {
        var hash = SettingsJson.Hash(bundle.ManifestBytes);
        ImportValidation.Require(configuration.GetSection("AdministrativeImport:TrustedManifestSha256").Get<string[]>()?.Contains(hash, StringComparer.Ordinal) == true,
            "untrusted_manifest");
        if (Profile == "Production")
            ImportValidation.Require(configuration["AdministrativeImport:OfficialReconciliationManifestSha256"] == hash
                && configuration["AdministrativeImport:RuntimeRoleVerified"] == "true"
                && configuration["AdministrativeImport:RestoreDrillVerified"] == "true", "production_import_hold");
    }
    public void VerifyApproval(string? hash, ImportApproval approval)
    {
        ImportValidation.Require(ImportValidation.Hash(hash)
            && configuration.GetSection("AdministrativeImport:TrustedApprovalSha256").Get<string[]>()?.Contains(hash!, StringComparer.Ordinal) == true,
            "untrusted_approval");
        if (Profile == "Production")
            ImportValidation.Require(!string.IsNullOrWhiteSpace(approval.BackupReference) && ImportValidation.Hash(approval.BackupSha256)
                && configuration["AdministrativeImport:VerifiedBackupSha256"] == approval.BackupSha256, "production_backup_missing");
    }
}
