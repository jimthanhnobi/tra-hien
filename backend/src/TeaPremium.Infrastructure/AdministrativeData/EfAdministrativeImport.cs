using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Results;
using TeaPremium.Application.Settings;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.AdministrativeData;

public sealed class EfAdministrativeImport(AppDbContext db, ImportEnvironment environment, IClock clock, IStaffIdentity staff) : IAdministrativeImport
{
    public async Task<Result<ImportPlan>> PlanAsync(ImportBundle bundle, CancellationToken ct)
    {
        try
        {
            var target = Verify(bundle, out var payload);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var state = await ReadStateAsync(false, bundle.Manifest(), payload.Catalog, ct);
            var plan = ImportPlanner.Create(bundle, payload, state, target, environment.Profile, await MigrationsAsync(ct)).Plan;
            await transaction.RollbackAsync(ct);
            return Result.Success(plan);
        }
        catch (AdministrativeCsvException e)
        {
            return Result.Failure<ImportPlan>(new("invalid_import_csv", "CSV không hợp lệ.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["csv"] = [$"Dòng {e.Line}: mã, tên hoặc cấu trúc không hợp lệ."] }));
        }
        catch (Exception e) when (InputFailure(e)) { return Failure<ImportPlan>(Code(e)); }
    }

    public async Task<Result<ImportOutcome>> ApplyAsync(ImportBundle bundle, ImportExecution execution, CancellationToken ct)
    {
        try
        {
            var target = Verify(bundle, out var payload);
            ImportValidation.Require(db.Database.CurrentTransaction is not null && !execution.DryRun, "missing_command_transaction");
            var transaction = db.Database.CurrentTransaction!;
            await using (var command = Command("SELECT pg_catalog.pg_try_advisory_xact_lock(8300, 9)"))
                if (await command.ExecuteScalarAsync(ct) is not true) return Failure<ImportOutcome>("import_busy");
            // No ledger table lock: SELECT/INSERT are sufficient. All importer paths use this same xact lock.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'; LOCK TABLE platform.provinces IN SHARE ROW EXCLUSIVE MODE; LOCK TABLE platform.wards IN SHARE ROW EXCLUSIVE MODE", ct);
            var state = await ReadStateAsync(true, bundle.Manifest(), payload.Catalog, ct);
            var tail = ImportPlanner.Tail(state.Events);
            var approval = execution.Approval ?? throw new ImportRejectedException("missing_approval");
            var m = bundle.Manifest();
            ImportValidation.Require(SettingsJson.Hash(ImportJson.Write(approval)) == execution.ApprovalSha256, "approval_checksum_mismatch");
            environment.VerifyApproval(execution.ApprovalSha256, approval);
            // Binding to an authenticated, revalidated existing staff session, not a typed operator name.
            var session = execution.OperatorId is { } actor ? await staff.SessionAsync(actor, ct) : null;
            ImportValidation.Require(session is not null && session.User.IsActive && !session.User.MustChangePassword
                && session.User.Permissions.Contains("settings.write", StringComparer.Ordinal)
                && session.User.Roles.Contains("owner", StringComparer.Ordinal)
                && approval.OperatorId == execution.OperatorId, "operator_not_authorized");
            var migrations = await MigrationsAsync(ct);
            ImportValidation.Require(execution.Plan.SchemaVersion == "vn-admin-plan/1" && execution.Plan.Profile == environment.Profile
                && execution.Plan.Target == target && execution.Plan.Migrations.SequenceEqual(migrations)
                && execution.Plan.ManifestSha256 == SettingsJson.Hash(bundle.ManifestBytes) && execution.Plan.CsvSha256 == SettingsJson.Hash(bundle.CsvBytes)
                && approval.PlanSha256 == execution.Plan.Fingerprint() && approval.Profile == environment.Profile && approval.Target == target
                && approval.RecoveryEventId == m.RecoveryEventId && approval.ReAdvanceOf == m.ReAdvanceOf && approval.PathPosition == m.PathPosition,
                "approval_plan_mismatch");
            if (tail is not null && tail.ManifestSha256 == execution.Plan.ManifestSha256 && tail.PlanSha256 == execution.Plan.Fingerprint()
                && tail.CsvSha256 == execution.Plan.CsvSha256)
            {
                ImportValidation.Require(tail.CatalogSha256 == state.Catalog.Digest(), "catalog_drift");
                using var receipt = JsonDocument.Parse(tail.Receipt);
                ImportValidation.Require(receipt.RootElement.GetProperty("approval_sha256").GetString() == execution.ApprovalSha256, "approval_plan_mismatch");
                return Result.Success(new ImportOutcome("no_op", execution.Plan, tail.Id));
            }
            // Approval freshness applies to a new event; exact committed retry uses its recorded approval.
            ImportValidation.Require(approval.ApprovedAt <= clock.UtcNow && approval.ApprovedAt >= clock.UtcNow.AddHours(-24), "stale_approval");
            var next = ImportPlanner.Create(bundle, payload, state, target, environment.Profile, migrations);
            ImportValidation.Require(next.Plan.Fingerprint() == execution.Plan.Fingerprint(), "stale_plan");
            CheckApprovalChanges(approval, next.Plan);
            await MutateAsync(state.Catalog, next.Catalog, ct);
            var id = Guid.CreateVersion7();
            db.AdministrativeDataImports.Add(new AdministrativeDataImport
            {
                Id = id,
                PreviousImportId = tail?.Id,
                DatasetVersion = m.DatasetVersion,
                ReleaseOrder = m.ReleaseOrder,
                EffectiveOn = m.EffectiveOn,
                Operation = m.Operation,
                Manifest = JsonSerializer.Serialize(m, ImportSerializer.Options),
                ManifestSha256 = next.Plan.ManifestSha256,
                CsvSha256 = next.Plan.CsvSha256,
                PlanSha256 = next.Plan.Fingerprint(),
                CatalogSha256 = next.Plan.AfterDigest,
                AppliedAt = clock.UtcNow.UtcDateTime,
                RecoveryOf = m.RecoveryOf,
                ReAdvanceOf = m.ReAdvanceOf,
                Receipt = SettingsJson.Serialize(new
                {
                    operator_id = approval.OperatorId,
                    approved_at = approval.ApprovedAt,
                    approval_sha256 = execution.ApprovalSha256,
                    target,
                    backup_reference = approval.BackupReference,
                    backup_sha256 = approval.BackupSha256,
                    before_digest = next.Plan.BeforeDigest,
                    after_digest = next.Plan.AfterDigest,
                    counts = next.Plan.Changes.GroupBy(x => x.Action).ToDictionary(x => x.Key, x => x.Count())
                })
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            var actual = await ReadCatalogAsync(ct);
            ImportValidation.Require(actual.Digest() == next.Plan.AfterDigest, "after_digest_mismatch");
            return Result.Success(new ImportOutcome("applied", next.Plan, id));
        }
        catch (Exception e) when (InputFailure(e)) { return Failure<ImportOutcome>(Code(e)); }
        // SQL/commit failures escape to the existing UoW, which rolls back data and ledger together.
    }

    private string Verify(ImportBundle bundle, out AdministrativePayload payload)
    {
        var target = environment.Target(db.Database.GetConnectionString() ?? "");
        environment.VerifyManifest(bundle);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).DateTime);
        payload = ImportValidation.Validate(bundle, today, environment.Profile == "Production");
        return target;
    }
    private async Task<string[]> MigrationsAsync(CancellationToken ct) => (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
    private async Task<AdministrativeCatalog> ReadCatalogAsync(CancellationToken ct) => new(
        await db.Provinces.AsNoTracking().OrderBy(x => x.Code).Select(x => new ProvinceData(x.Code, x.Name, x.IsActive, x.SortOrder)).ToArrayAsync(ct),
        await db.Wards.AsNoTracking().OrderBy(x => x.Code).Select(x => new WardData(x.Code, x.ProvinceCode, x.Name, x.IsActive)).ToArrayAsync(ct));
    private async Task<ImportState> ReadStateAsync(bool lockReferences, ImportManifest manifest, AdministrativeCatalog input, CancellationToken ct)
    {
        var catalog = await ReadCatalogAsync(ct);
        var affectedProvinces = catalog.Provinces.Where(p => manifest.Deactivate.Provinces.Contains(p.Code, StringComparer.Ordinal)
            || manifest.Mode == "full" && !input.Provinces.Any(n => n.Code == p.Code)
            || input.Provinces.Any(n => n.Code == p.Code && (n.Name != p.Name || !p.IsActive))).Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var affectedWards = catalog.Wards.Where(w => manifest.Deactivate.Wards.Contains(w.Code, StringComparer.Ordinal)
            || manifest.Mode == "full" && !input.Wards.Any(n => n.Code == w.Code)
            || input.Wards.Any(n => n.Code == w.Code && (n.Name != w.Name || n.ProvinceCode != w.ProvinceCode || !w.IsActive))).Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
        var events = (await db.AdministrativeDataImports.AsNoTracking().ToArrayAsync(ct)).Select(e => new ImportEvent(e.Id, e.PreviousImportId,
            ImportJson.Read<ImportManifest>(System.Text.Encoding.UTF8.GetBytes(e.Manifest)), e.ManifestSha256, e.CsvSha256, e.PlanSha256, e.CatalogSha256, e.Receipt, e.AppliedAt)).ToArray();
        var refs = new List<object[]>();
        foreach (var table in new[] { "crm.customer_addresses", "sales.orders" })
        {
            await using var existence = Command("SELECT to_regclass(@table)::text");
            existence.Parameters.AddWithValue("table", table);
            if (await existence.ExecuteScalarAsync(ct) is not string) { refs.Add([table, "not yet present"]); continue; }
            if (lockReferences) await db.Database.ExecuteSqlRawAsync(table == "crm.customer_addresses"
                ? "LOCK TABLE crm.customer_addresses IN SHARE MODE" : "LOCK TABLE sales.orders IN SHARE MODE", ct);
            await using var command = Command(table == "crm.customer_addresses"
                ? "SELECT id::text,province_code,coalesce(ward_code,'') FROM crm.customer_addresses ORDER BY id"
                : "SELECT id::text,province_code,coalesce(ward_code,'') FROM sales.orders ORDER BY id");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (affectedProvinces.Contains(reader.GetString(1)) || affectedWards.Contains(reader.GetString(2)))
                    refs.Add([table, reader.GetString(0), reader.GetString(1), reader.GetString(2)]);
        }
        return new(catalog, events, SettingsJson.Hash(SettingsJson.Serialize(refs)), refs.Count(x => x.Length == 4));
    }
    private async Task MutateAsync(AdministrativeCatalog before, AdministrativeCatalog after, CancellationToken ct)
    {
        var ps = (await db.Provinces.ToArrayAsync(ct)).ToDictionary(x => x.Code, StringComparer.Ordinal);
        foreach (var p in after.Provinces)
        {
            if (before.Provinces.Contains(p)) continue;
            if (!ps.TryGetValue(p.Code, out var row)) { row = new Province { Code = p.Code }; db.Provinces.Add(row); }
            row.Name = p.Name; row.IsActive = p.IsActive; row.SortOrder = p.SortOrder;
        }
        await db.SaveChangesAsync(ct);
        var ws = (await db.Wards.ToArrayAsync(ct)).ToDictionary(x => x.Code, StringComparer.Ordinal);
        foreach (var w in after.Wards)
        {
            if (before.Wards.Contains(w)) continue;
            if (!ws.TryGetValue(w.Code, out var row)) { row = new Ward { Code = w.Code }; db.Wards.Add(row); }
            row.Name = w.Name; row.ProvinceCode = w.ProvinceCode; row.IsActive = w.IsActive;
        }
        await db.SaveChangesAsync(ct);
    }
    private NpgsqlCommand Command(string sql)
        => new(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
    private static void CheckApprovalChanges(ImportApproval approval, ImportPlan plan)
    {
        static string[] Codes(ImportPlan p, string kind, string action) => p.Changes.Where(x => (kind == "*" || x.Kind == kind) && x.Action == action).Select(x => x.Code).Order(StringComparer.Ordinal).ToArray();
        ImportValidation.Require(approval.DeactivateProvinces.Order(StringComparer.Ordinal).SequenceEqual(Codes(plan, "province", "deactivate"))
            && approval.DeactivateWards.Order(StringComparer.Ordinal).SequenceEqual(Codes(plan, "ward", "deactivate"))
            && approval.ReparentWards.Order(StringComparer.Ordinal).SequenceEqual(Codes(plan, "ward", "reparent"))
            && approval.ReactivateCodes.Order(StringComparer.Ordinal).SequenceEqual(Codes(plan, "*", "reactivate")), "unapproved_changes");
    }
    private static bool InputFailure(Exception e) => e is ImportRejectedException or FormatException or JsonException or System.Text.DecoderFallbackException or ArgumentException or NullReferenceException;
    private static string Code(Exception e) => e is ImportRejectedException rejection ? rejection.Code : "invalid_import_input";
    private static Result<T> Failure<T>(string code) => Result.Failure<T>(new(code, "Nhập dữ liệu không được chấp nhận; kiểm tra hồ sơ và dry-run.", ErrorType.Validation));
}

internal static class ImportSerializer
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
