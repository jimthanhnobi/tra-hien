using TeaPremium.Application.Settings;

namespace TeaPremium.Application.AdministrativeData;

public sealed record PlannedCatalog(ImportPlan Plan, AdministrativeCatalog Catalog);

public static class ImportPlanner
{
    public static ImportEvent? Tail(ImportEvent[] events)
    {
        if (events.Length == 0) return null;
        var predecessors = events.Where(x => x.PreviousImportId.HasValue).Select(x => x.PreviousImportId!.Value).ToHashSet();
        var tails = events.Where(x => !predecessors.Contains(x.Id)).ToArray();
        ImportValidation.Require(tails.Length == 1 && events.Count(x => x.PreviousImportId is null) == 1, "invalid_ledger_chain");
        var byId = events.ToDictionary(x => x.Id);
        var walked = new HashSet<Guid>();
        ImportEvent? next = tails[0];
        while (next is not null)
        {
            ImportValidation.Require(walked.Add(next.Id), "invalid_ledger_chain");
            next = next.PreviousImportId is { } prev ? byId.GetValueOrDefault(prev) ?? throw new ImportRejectedException("invalid_ledger_chain") : null;
        }
        ImportValidation.Require(walked.Count == events.Length, "invalid_ledger_chain");
        return tails[0];
    }

    public static PlannedCatalog Create(ImportBundle bundle, AdministrativePayload payload, ImportState state,
        string target, string profile, string[] migrations)
    {
        var m = bundle.Manifest();
        var tail = Tail(state.Events);
        ImportValidation.Require(m.BaseImportId == tail?.Id, "stale_plan");
        ValidateRelease(m, state.Events, tail);
        ImportValidation.Require(tail is not null || state.Catalog.Provinces.Length + state.Catalog.Wards.Length == 0, "unregistered_catalog");
        if (tail is not null) ImportValidation.Require(tail.CatalogSha256 == state.Catalog.Digest(), "catalog_drift");
        var ps = state.Catalog.Provinces.ToDictionary(p => p.Code, StringComparer.Ordinal);
        var ws = state.Catalog.Wards.ToDictionary(w => w.Code, StringComparer.Ordinal);
        var changes = new List<CatalogChange>();
        foreach (var p in payload.Catalog.Provinces)
        {
            ps.TryGetValue(p.Code, out var before);
            var after = p with { SortOrder = before?.SortOrder ?? 0 };
            ps[p.Code] = after;
            Changes(changes, "province", p.Code, before, after);
        }
        foreach (var w in payload.Catalog.Wards)
        {
            ws.TryGetValue(w.Code, out var before);
            ws[w.Code] = w;
            Changes(changes, "ward", w.Code, before, w);
        }
        var pMissing = m.Mode == "full" ? state.Catalog.Provinces.Where(p => !payload.Catalog.Provinces.Any(x => x.Code == p.Code)).Select(x => x.Code).ToArray() : [];
        var wMissing = m.Mode == "full" ? state.Catalog.Wards.Where(w => !payload.Catalog.Wards.Any(x => x.Code == w.Code)).Select(x => x.Code).ToArray() : [];
        foreach (var code in pMissing.Concat(m.Deactivate.Provinces).Distinct(StringComparer.Ordinal))
        {
            ImportValidation.Require(ps.TryGetValue(code, out var p), "unknown_deactivation");
            if (p!.IsActive) { ps[code] = p with { IsActive = false }; changes.Add(new("province", code, "deactivate", p.Name, p.Name)); }
            if (pMissing.Contains(code, StringComparer.Ordinal)) changes.Add(new("province", code, "missing", p.Name, null));
        }
        foreach (var code in wMissing.Concat(m.Deactivate.Wards).Distinct(StringComparer.Ordinal))
        {
            ImportValidation.Require(ws.TryGetValue(code, out var w), "unknown_deactivation");
            if (w!.IsActive) { ws[code] = w with { IsActive = false }; changes.Add(new("ward", code, "deactivate", w.Name, w.Name)); }
            if (wMissing.Contains(code, StringComparer.Ordinal)) changes.Add(new("ward", code, "missing", w.Name, null));
        }
        ImportValidation.Require(ws.Values.All(w => ps.TryGetValue(w.ProvinceCode, out var p) && (!w.IsActive || p.IsActive)), "invalid_active_hierarchy");
        ImportValidation.Require(!changes.Any(x => x.Action is "reparent" or "reactivate") || !string.IsNullOrWhiteSpace(m.ChangeEvidence), "missing_change_evidence");
        var result = new AdministrativeCatalog(ps.Values.OrderBy(x => x.Code, StringComparer.Ordinal).ToArray(), ws.Values.OrderBy(x => x.Code, StringComparer.Ordinal).ToArray());
        var originals = state.Events.Where(x => x.Manifest.Operation == "import").ToArray();
        return new(new("vn-admin-plan/1", profile, target, migrations, tail?.Id, SettingsJson.Hash(bundle.ManifestBytes),
            SettingsJson.Hash(bundle.CsvBytes), "vn-admin-importer/1", state.Catalog.Digest(), result.Digest(), state.ReferenceDigest, state.ReferenceCount,
            originals.Length == 0 ? 0 : originals.Max(x => x.Manifest.ReleaseOrder),
            originals.Length == 0 ? null : originals.Max(x => x.Manifest.EffectiveOn), tail?.Manifest.ReleaseOrder,
            changes.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Action, StringComparer.Ordinal).ToArray(),
            result.Provinces.Length, result.Wards.Length, [], m.RecoveryEventId, m.ReAdvanceOf, m.PathPosition), result);
    }

    public static void ValidateRelease(ImportManifest m, ImportEvent[] events, ImportEvent? tail)
    {
        var originals = events.Where(e => e.Manifest.Operation == "import").ToArray();
        var same = originals.Where(e => e.Manifest.Identity() == m.Identity()).ToArray();
        ImportValidation.Require(same.All(e => e.Manifest.PayloadIdentity() == m.PayloadIdentity()), "release_payload_changed");
        if (m.Operation == "import")
        {
            ImportValidation.Require(m.RecoveryOf is null && m.ReAdvanceOf is null && same.Length == 0, "release_replay_rejected");
            ImportValidation.Require(originals.All(e => m.ReleaseOrder > e.Manifest.ReleaseOrder && m.EffectiveOn >= e.Manifest.EffectiveOn), "release_downgrade");
            ImportValidation.Require(!originals.Any(e => e.Manifest.EffectiveOn == m.EffectiveOn) || !string.IsNullOrWhiteSpace(m.ChangeEvidence), "missing_correction_evidence");
            return;
        }
        ImportValidation.Require(tail is not null, "recovery_without_history");
        if (m.Operation == "recovery")
        {
            var target = originals.SingleOrDefault(e => e.Id == m.TargetImportId);
            // Owner decision 18/11/2026: a delta payload cannot reconstruct a previous
            // complete catalog. Recovery targets must be verified full snapshots.
            ImportValidation.Require(m.Mode == "full" && target?.Manifest.Mode == "full", "recovery_requires_full_snapshot");
            ImportValidation.Require(target is not null && target.Manifest.Identity() == m.Identity()
                && target.Manifest.PayloadIdentity() == m.PayloadIdentity() && events.Any(e => e.Id == m.RecoveryOf)
                && !string.IsNullOrWhiteSpace(m.Reason) && !string.IsNullOrWhiteSpace(m.IncidentRecord)
                && m.ReAdvanceOf is null && m.ReAdvancePath is not null, "invalid_recovery");
            var order = m.ReleaseOrder;
            var date = m.EffectiveOn;
            foreach (var id in m.ReAdvancePath!)
            {
                var e = originals.SingleOrDefault(e => e.Id == id);
                ImportValidation.Require(e is not null && e.Manifest.ReleaseOrder > order && e.Manifest.EffectiveOn >= date, "invalid_recovery_path");
                order = e!.Manifest.ReleaseOrder; date = e.Manifest.EffectiveOn;
            }
            return;
        }
        var recovery = events.SingleOrDefault(e => e.Id == m.RecoveryEventId && e.Manifest.Operation == "recovery");
        ImportValidation.Require(recovery is not null && m.RecoveryOf is null && m.PathPosition >= 0, "invalid_readvance_path");
        var path = recovery!.Manifest.ReAdvancePath!;
        var nextPosition = tail!.Id == recovery.Id ? 0 : tail.Manifest.Operation == "re_advance"
            && tail.Manifest.RecoveryEventId == recovery.Id ? (tail.Manifest.PathPosition ?? -1) + 1 : -1;
        ImportValidation.Require(nextPosition >= 0 && nextPosition < path.Length && m.PathPosition == nextPosition
            && path[nextPosition] == m.ReAdvanceOf && m.ReleaseOrder > tail.Manifest.ReleaseOrder && m.EffectiveOn >= tail.Manifest.EffectiveOn, "invalid_readvance_path");
        var original = originals.SingleOrDefault(e => e.Id == m.ReAdvanceOf);
        ImportValidation.Require(original is not null && original.Manifest.Identity() == m.Identity()
            && original.Manifest.PayloadIdentity() == m.PayloadIdentity(), "release_payload_changed");
        if (m.Mode == "delta")
        {
            var originalBase = events.Single(e => e.Id == original!.PreviousImportId);
            ImportValidation.Require(originalBase.Manifest.Identity() == tail.Manifest.Identity()
                && originalBase.Manifest.PayloadIdentity() == tail.Manifest.PayloadIdentity(), "invalid_delta_base");
        }
    }

    private static void Changes(List<CatalogChange> changes, string kind, string code, object? before, object after)
    {
        if (before is null) { changes.Add(new(kind, code, "insert", null, SettingsJson.Serialize(after))); return; }
        var oldName = before is ProvinceData p ? p.Name : ((WardData)before).Name;
        var newName = after is ProvinceData q ? q.Name : ((WardData)after).Name;
        var active = before is ProvinceData r ? r.IsActive : ((WardData)before).IsActive;
        if (oldName != newName) changes.Add(new(kind, code, "rename", oldName, newName));
        if (!active) changes.Add(new(kind, code, "reactivate", null, newName));
        if (before is WardData w && after is WardData n && w.ProvinceCode != n.ProvinceCode)
            changes.Add(new(kind, code, "reparent", w.ProvinceCode, n.ProvinceCode));
        if (before.Equals(after)) changes.Add(new(kind, code, "unchanged", oldName, newName));
    }
}
