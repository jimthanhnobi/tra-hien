using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

internal interface ICustomerImportSaveHook
{
    Task BeforeSaveAttemptAsync(int attempt, CancellationToken ct);
}

internal sealed class CustomerImportZaloRow
{
    public string FullName { get; init; } = "";
    public string ZaloName { get; init; } = "";
}

public sealed class EfCustomerImportStore : ICustomerImportStore
{
    private const long CustomerImportLockKey = 8_203_001L;
    private const int MaxSaveAttempts = 3;

    // Exact 25 Unicode whitespace characters trimmed by .NET string.Trim() (char.IsWhiteSpace), locale-independent in PG.
    internal const string DotNetWhitespaceChars =
        "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680" +
        "\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A" +
        "\u2028\u2029\u202F\u205F\u3000";

    private readonly AppDbContext db;
    private readonly ICustomerImportSaveHook? saveHook;

    public EfCustomerImportStore(AppDbContext db) : this(db, null)
    {
    }

    internal EfCustomerImportStore(AppDbContext db, ICustomerImportSaveHook? saveHook)
    {
        this.db = db;
        this.saveHook = saveHook;
    }

    public async Task<CustomerImportResult> PreviewAsync(IReadOnlyList<CustomerImportLine> lines, CancellationToken ct)
    {
        var validated = CustomerImportPlanner.ValidateLines(lines);
        var candidatePhones = ExtractCandidatePhones(validated);
        var existingPhones = await LoadExistingActivePhonesAsync(candidatePhones, ct);
        var candidateZaloKeys = ExtractCandidateZaloKeys(validated);
        var existingZaloKeys = await LoadExistingActiveZaloKeysAsync(candidateZaloKeys, ct);

        return await BuildPreviewResultAsync(validated, existingPhones, existingZaloKeys, dryRun: true, ct);
    }

    public async Task<Result<CustomerImportResult>> ApplyAsync(
        IReadOnlyList<CustomerImportLine> lines,
        Guid actorId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Customer import requires an active transaction.");

        // Serialize concurrent legacy imports cleanly before reading mutable customer/tag state.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_catalog.pg_advisory_xact_lock({CustomerImportLockKey})", ct);

        var validated = CustomerImportPlanner.ValidateLines(lines);
        var candidatePhones = ExtractCandidatePhones(validated);
        var existingPhones = await LoadExistingActivePhonesAsync(candidatePhones, ct);
        var candidateZaloKeys = ExtractCandidateZaloKeys(validated);
        var existingZaloKeys = await LoadExistingActiveZaloKeysAsync(candidateZaloKeys, ct);

        // O4.1=B: stop early before sequence allocation or any DB write when at least one row has validation errors.
        if (validated.Any(v => v.Errors.Count > 0))
        {
            var blockedResult = await BuildPreviewResultAsync(validated, existingPhones, existingZaloKeys, dryRun: false, ct);
            return Result.Success(blockedResult);
        }

        var planned = CustomerImportPlanner.Plan(validated, existingPhones, dryRun: false, existingZaloKeys);
        var toImportIndices = CollectImportIndices(planned);
        if (toImportIndices.Length == 0)
        {
            var emptyRows = planned.Select(BuildNonImportedRow).ToArray();
            var emptySummary = CustomerImportPlanner.BuildSummary(
                dryRun: false,
                emptyRows,
                createdTagsCount: 0,
                createdInteractionsCount: 0,
                newTags: []);
            return Result.Success(new CustomerImportResult(emptySummary, emptyRows));
        }

        // Batch-allocate KH codes and compute BR-CRM-14 search_text once to avoid N+1 SQL round-trips.
        var sequences = await db.Database
            .SqlQuery<long>($"SELECT nextval('crm.customer_code_seq') AS \"Value\" FROM generate_series(1, {toImportIndices.Length})")
            .ToArrayAsync(ct);

        if (sequences.Length != toImportIndices.Length)
        {
            throw new InvalidOperationException("Allocated customer code count does not match requested batch size.");
        }

        var allocatedCodes = new Dictionary<int, string>(toImportIndices.Length);
        var searchSources = new string[toImportIndices.Length];
        for (var k = 0; k < toImportIndices.Length; k++)
        {
            var idx = toImportIndices[k];
            var v = planned[idx].Validated!;
            var code = CustomerRules.FormatCustomerCode(sequences[k]);
            allocatedCodes[idx] = code;
            searchSources[k] = CustomerRules.BuildSearchSource(v.FullName, v.ZaloName, code, v.NormalizedPhone, email: null);
        }

        var unaccented = await db.Database
            .SqlQuery<string>($"SELECT public.f_unaccent(src) AS \"Value\" FROM unnest({searchSources}) WITH ORDINALITY AS t(src, ord) ORDER BY ord")
            .ToArrayAsync(ct);

        if (unaccented.Length != toImportIndices.Length)
        {
            throw new InvalidOperationException("Computed search_text count does not match requested batch size.");
        }

        var searchTexts = new Dictionary<int, string>(toImportIndices.Length);
        for (var k = 0; k < toImportIndices.Length; k++)
        {
            searchTexts[toImportIndices[k]] = unaccented[k];
        }

        for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
        {
            var survivingIndices = CollectImportIndices(planned);
            if (survivingIndices.Length == 0)
            {
                var skippedRows = planned.Select(BuildNonImportedRow).ToArray();
                var skippedSummary = CustomerImportPlanner.BuildSummary(
                    dryRun: false,
                    skippedRows,
                    createdTagsCount: 0,
                    createdInteractionsCount: 0,
                    newTags: []);
                return Result.Success(new CustomerImportResult(skippedSummary, skippedRows));
            }

            var hasTags = survivingIndices.Any(idx => planned[idx].Validated is { Tags.Length: > 0 });
            var (existingTagsOrdinal, existingTagsIgnoreCase) = hasTags
                ? await LoadExistingTagsAsync(ct)
                : (new Dictionary<string, Tag>(StringComparer.Ordinal), new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase));

            var newTagsByIgnoreCase = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
            var newTags = new List<Tag>();
            var newCustomers = new List<Customer>(survivingIndices.Length);
            var newCustomerTags = new List<CustomerTag>();
            var newInteractions = new List<Interaction>();
            var committed = new Dictionary<int, (Guid CustomerId, string Code, string[] Tags, bool HasNote)>(survivingIndices.Length);

            foreach (var idx in survivingIndices)
            {
                var item = planned[idx];
                var v = item.Validated!;
                var customerId = Guid.CreateVersion7();
                var code = allocatedCodes[idx];
                var searchText = searchTexts[idx];

                newCustomers.Add(new Customer
                {
                    Id = customerId,
                    Code = code,
                    FullName = v.FullName,
                    PhoneE164 = v.NormalizedPhone,
                    Email = null,
                    ZaloName = v.ZaloName,
                    CustomerGroup = v.CustomerGroup,
                    IsVip = false,
                    Origin = "legacy_import",
                    AcquisitionSource = "legacy",
                    ReferredByCustomerId = null,
                    ContactStatus = "active",
                    NoteSummary = null,
                    HadLegacyPurchase = v.HadPurchase,
                    FirstDeliveredAt = null,
                    LastDeliveredAt = null,
                    DeliveredOrdersCount = 0,
                    LifetimeValueVnd = 0,
                    LastContactedAt = null, // BR-CRM-07: kind='note' never updates last_contacted_at.
                    SearchText = searchText,
                    MergedIntoCustomerId = null,
                    ErasedAt = null,
                    CreatedBy = actorId,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                });

                var resolvedTagNames = new string[v.Tags.Length];
                var attachedTagIds = new HashSet<Guid>();
                var tagCount = 0;
                foreach (var rawTagName in v.Tags)
                {
                    if (!existingTagsOrdinal.TryGetValue(rawTagName, out var tag)
                        && !existingTagsIgnoreCase.TryGetValue(rawTagName, out tag)
                        && !newTagsByIgnoreCase.TryGetValue(rawTagName, out tag))
                    {
                        tag = new Tag
                        {
                            Id = Guid.CreateVersion7(),
                            Name = rawTagName,
                            Color = null
                        };
                        newTagsByIgnoreCase[rawTagName] = tag;
                        newTags.Add(tag);
                    }

                    if (attachedTagIds.Add(tag.Id))
                    {
                        newCustomerTags.Add(new CustomerTag { CustomerId = customerId, TagId = tag.Id });
                        resolvedTagNames[tagCount++] = tag.Name;
                    }
                }

                if (tagCount != resolvedTagNames.Length)
                {
                    Array.Resize(ref resolvedTagNames, tagCount);
                }

                var hasNote = v.Note is not null;
                if (hasNote)
                {
                    // BR-CRM-03/06/07 & O4.2=A: create 1 interaction(kind='note', channel='other').
                    newInteractions.Add(new Interaction
                    {
                        Id = Guid.CreateVersion7(),
                        CustomerId = customerId,
                        StaffId = actorId,
                        Channel = "other",
                        Kind = "note",
                        Content = v.Note!,
                        OccurredAt = nowUtc,
                        OrderId = null,
                        CampaignId = null,
                        FollowUpAt = null,
                        FollowUpDoneAt = null,
                        FollowUpResult = null,
                        CreatedAt = nowUtc
                    });
                }

                committed[idx] = (customerId, code, resolvedTagNames, hasNote);
            }

            if (saveHook is not null)
            {
                await saveHook.BeforeSaveAttemptAsync(attempt, ct);
            }

            var savepoint = $"p203_apply_batch_{attempt}";
            await tx.CreateSavepointAsync(savepoint, ct);
            var previousAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
            try
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                if (newTags.Count > 0) db.AddRange(newTags);
                db.AddRange(newCustomers);
                if (newCustomerTags.Count > 0) db.AddRange(newCustomerTags);
                if (newInteractions.Count > 0) db.AddRange(newInteractions);

                await db.SaveChangesAsync(ct);
                await tx.ReleaseSavepointAsync(savepoint, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraint(ex, "ux_tags_name") || IsUniqueConstraint(ex, "ux_customers_phone"))
            {
                await tx.RollbackToSavepointAsync(savepoint, ct);
                await tx.ReleaseSavepointAsync(savepoint, ct);
                db.ChangeTracker.Clear();

                if (attempt == MaxSaveAttempts)
                {
                    return Result.Failure<CustomerImportResult>(ConcurrencyError());
                }

                // Keep existingPhones monotonically non-shrinking so survivingIndices always remains a subset of toImportIndices.
                var survivingPhones = ExtractPhonesForIndices(planned, survivingIndices);
                var newlyAppearedPhones = await LoadExistingActivePhonesAsync(survivingPhones, ct);
                foreach (var (phone, existingId) in newlyAppearedPhones)
                {
                    existingPhones.TryAdd(phone, existingId);
                }

                planned = CustomerImportPlanner.Plan(validated, existingPhones, dryRun: false, existingZaloKeys);
                continue;
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }

            var resultRows = new CustomerImportRow[planned.Length];
            for (var i = 0; i < planned.Length; i++)
            {
                var item = planned[i];
                if (!committed.TryGetValue(i, out var info))
                {
                    resultRows[i] = BuildNonImportedRow(item);
                    continue;
                }

                resultRows[i] = new CustomerImportRow(
                    item.Row,
                    "imported",
                    "imported",
                    info.CustomerId,
                    info.Code,
                    item.Validated!.NormalizedPhone,
                    null,
                    null,
                    info.HasNote,
                    info.Tags,
                    item.Warnings,
                    []);
            }

            var createdTagNames = newTags.Select(t => t.Name).ToArray();
            var summary = CustomerImportPlanner.BuildSummary(
                dryRun: false,
                resultRows,
                createdTagsCount: newTags.Count,
                createdInteractionsCount: newInteractions.Count,
                newTags: createdTagNames);
            return Result.Success(new CustomerImportResult(summary, resultRows));
        }

        return Result.Failure<CustomerImportResult>(ConcurrencyError());
    }

    private async Task<CustomerImportResult> BuildPreviewResultAsync(
        CustomerImportValidatedLine[] validated,
        Dictionary<string, Guid> existingPhones,
        HashSet<ZaloIdentityKey> existingZaloKeys,
        bool dryRun,
        CancellationToken ct)
    {
        var planned = CustomerImportPlanner.Plan(validated, existingPhones, dryRun: true, existingZaloKeys);

        var hasReadyTags = planned.Any(p => p.Status == "would_import" && p.Validated is { Tags.Length: > 0 });
        var (existingTagsOrdinal, existingTagsIgnoreCase) = hasReadyTags
            ? await LoadExistingTagsAsync(ct)
            : (new Dictionary<string, Tag>(StringComparer.Ordinal), new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase));

        var previewNewTagsByIgnoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previewNewTags = new List<string>();
        var rows = new CustomerImportRow[planned.Length];

        for (var i = 0; i < planned.Length; i++)
        {
            var item = planned[i];
            if (item.Status != "would_import")
            {
                rows[i] = BuildNonImportedRow(item);
                continue;
            }

            var v = item.Validated!;
            var resolvedTags = ResolvePreviewTags(
                v.Tags,
                existingTagsOrdinal,
                existingTagsIgnoreCase,
                previewNewTagsByIgnoreCase,
                previewNewTags);
            rows[i] = new CustomerImportRow(
                item.Row,
                item.Status,
                item.ReasonCode,
                null,
                null,
                v.NormalizedPhone,
                null,
                null,
                v.Note is not null,
                resolvedTags,
                item.Warnings,
                []);
        }

        var summary = CustomerImportPlanner.BuildSummary(
            dryRun: dryRun,
            rows,
            createdTagsCount: 0,
            createdInteractionsCount: 0,
            newTags: previewNewTags);
        return new CustomerImportResult(summary, rows);
    }

    private async Task<Dictionary<string, Guid>> LoadExistingActivePhonesAsync(string[] candidatePhones, CancellationToken ct)
    {
        if (candidatePhones.Length == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        var existing = await db.Set<Customer>().AsNoTracking()
            .Where(x => x.PhoneE164 != null && x.MergedIntoCustomerId == null && candidatePhones.Contains(x.PhoneE164))
            .Select(x => new { Phone = x.PhoneE164!, x.Id })
            .ToArrayAsync(ct);

        var map = new Dictionary<string, Guid>(existing.Length, StringComparer.Ordinal);
        foreach (var item in existing)
        {
            map.TryAdd(item.Phone, item.Id);
        }

        return map;
    }

    private async Task<HashSet<ZaloIdentityKey>> LoadExistingActiveZaloKeysAsync(
        ZaloIdentityKey[] candidateKeys,
        CancellationToken ct)
    {
        var result = new HashSet<ZaloIdentityKey>();
        if (candidateKeys.Length == 0)
        {
            return result;
        }

        var candidateSet = new HashSet<ZaloIdentityKey>(candidateKeys);
        var candidateFullNames = candidateKeys
            .Select(k => k.FullNameNormalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateZaloNames = candidateKeys
            .Select(k => k.ZaloNameNormalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var whitespaceChars = DotNetWhitespaceChars;
        var matches = await db.Database
            .SqlQuery<CustomerImportZaloRow>(
                $"""
                SELECT c.full_name AS "FullName", c.zalo_name AS "ZaloName"
                FROM crm.customers c
                WHERE c.merged_into_customer_id IS NULL
                  AND c.phone_e164 IS NULL
                  AND c.zalo_name IS NOT NULL
                  AND public.f_unaccent(btrim(c.zalo_name, {whitespaceChars})) IN (
                      SELECT public.f_unaccent(z) FROM unnest({candidateZaloNames}) AS z
                  )
                  AND public.f_unaccent(btrim(c.full_name, {whitespaceChars})) IN (
                      SELECT public.f_unaccent(f) FROM unnest({candidateFullNames}) AS f
                  )
                """)
            .ToArrayAsync(ct);

        foreach (var row in matches)
        {
            var key = ZaloIdentityKey.Create(row.FullName, row.ZaloName);
            if (candidateSet.Contains(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private async Task<(Dictionary<string, Tag> ByOrdinal, Dictionary<string, Tag> ByIgnoreCase)> LoadExistingTagsAsync(CancellationToken ct)
    {
        var tags = await db.Set<Tag>().AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        var byOrdinal = new Dictionary<string, Tag>(tags.Length, StringComparer.Ordinal);
        var byIgnoreCase = new Dictionary<string, Tag>(tags.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            byOrdinal.TryAdd(tag.Name, tag);
            byIgnoreCase.TryAdd(tag.Name, tag);
        }

        return (byOrdinal, byIgnoreCase);
    }

    private static string[] ResolvePreviewTags(
        string[] rawTags,
        Dictionary<string, Tag> existingOrdinal,
        Dictionary<string, Tag> existingIgnoreCase,
        Dictionary<string, string> previewNewTagsByIgnoreCase,
        List<string> previewNewTags)
    {
        if (rawTags.Length == 0)
        {
            return [];
        }

        var resolved = new List<string>(rawTags.Length);
        var seenOnRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in rawTags)
        {
            string canonical;
            if (existingOrdinal.TryGetValue(name, out var exact))
            {
                canonical = exact.Name;
            }
            else if (existingIgnoreCase.TryGetValue(name, out var ci))
            {
                canonical = ci.Name;
            }
            else if (previewNewTagsByIgnoreCase.TryGetValue(name, out var firstNew))
            {
                canonical = firstNew;
            }
            else
            {
                previewNewTagsByIgnoreCase[name] = name;
                previewNewTags.Add(name);
                canonical = name;
            }

            if (seenOnRow.Add(canonical))
            {
                resolved.Add(canonical);
            }
        }

        return [.. resolved];
    }

    private static string[] ExtractCandidatePhones(CustomerImportValidatedLine[] validated)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in validated)
        {
            if (line.Errors.Count == 0 && line.NormalizedPhone is { } phone)
            {
                set.Add(phone);
            }
        }

        return [.. set];
    }

    private static ZaloIdentityKey[] ExtractCandidateZaloKeys(CustomerImportValidatedLine[] validated)
    {
        var set = new HashSet<ZaloIdentityKey>();
        foreach (var line in validated)
        {
            if (line.Errors.Count == 0 && line.NormalizedPhone is null && line.ZaloName is { } zaloName)
            {
                set.Add(ZaloIdentityKey.Create(line.FullName, zaloName));
            }
        }

        return [.. set];
    }

    private static string[] ExtractPhonesForIndices(CustomerImportPlannedRow[] planned, int[] indices)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var idx in indices)
        {
            if (planned[idx].Validated?.NormalizedPhone is { } phone)
            {
                set.Add(phone);
            }
        }

        return [.. set];
    }

    private static int[] CollectImportIndices(CustomerImportPlannedRow[] planned)
    {
        var indices = new List<int>();
        for (var i = 0; i < planned.Length; i++)
        {
            if (string.Equals(planned[i].Status, "imported", StringComparison.Ordinal))
            {
                indices.Add(i);
            }
        }

        return [.. indices];
    }

    private static Error ConcurrencyError()
        => new("concurrency_conflict", "Dữ liệu khách hàng đã thay đổi đồng thời, vui lòng thử lại.", ErrorType.Conflict);

    private static CustomerImportRow BuildNonImportedRow(CustomerImportPlannedRow item) => new(
        item.Row,
        item.Status,
        item.ReasonCode,
        null,
        null,
        item.Validated?.NormalizedPhone,
        item.ExistingCustomerId,
        item.DuplicateOfRow,
        false,
        [],
        item.Warnings,
        item.Errors);

    private static bool IsUniqueConstraint(DbUpdateException ex, string constraintName)
        => ex.InnerException is PostgresException pg
           && pg.SqlState == PostgresErrorCodes.UniqueViolation
           && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);
}
