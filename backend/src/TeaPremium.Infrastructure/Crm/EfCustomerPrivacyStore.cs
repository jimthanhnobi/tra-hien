using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

// 02 §3 / CrmModuleBoundaryRule: strictly accesses only schema crm entities and platform.audit_logs.
public sealed class EfCustomerPrivacyStore(
    AppDbContext db,
    ICustomerQueries queries,
    CustomerPrivacyAudit auditWriter) : ICustomerPrivacyStore
{
    private static readonly long PrivacyAdvisoryLockKey = BinaryPrimitives.ReadInt64BigEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes("TeaPremium.CrmPrivacy:MergeErase")));

    private readonly Dictionary<Guid, Customer> _lockedCustomers = [];
    private readonly Dictionary<Guid, CustomerAccount> _lockedAccountsByCustomer = [];
    private List<Customer> _mergeChainStubs = [];
    private List<Customer> _mergeReferrers = [];
    private List<Customer> _eraseMergedStubs = [];
    private List<CustomerAccount> _eraseAccounts = [];
    private Guid[] _eraseAllCustomerIds = [];

    public async Task AcquirePrivacyLockAsync(CancellationToken ct)
    {
        RequireTransaction();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_catalog.pg_advisory_xact_lock({PrivacyAdvisoryLockKey})", ct);
    }

    public async Task<CustomerRecord?> LockCustomerForConsentAsync(Guid customerId, CancellationToken ct)
    {
        RequireTransaction();
        var row = (await db.Set<Customer>()
            .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = {customerId} FOR NO KEY UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        _lockedCustomers[customerId] = row;
        return ToRecord(row);
    }

    public async Task<Result<CustomerConsentsResult>> UpsertConsentAsync(
        Guid customerId,
        string channel,
        string purpose,
        string status,
        string evidence,
        uint? expectedVersion,
        Guid actorId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = RequireTransaction();
        var customer = _lockedCustomers[customerId];

        const string savepoint = "p212_put_consent";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            var existing = await db.Set<Consent>()
                .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.Channel == channel && x.Purpose == purpose, ct);

            Consent targetConsent;
            if (existing is not null)
            {
                existing.Status = status;
                existing.Source = "staff";
                existing.Evidence = evidence;
                existing.CapturedAt = nowUtc;
                existing.CapturedBy = actorId;
                targetConsent = existing;
            }
            else
            {
                targetConsent = new Consent
                {
                    Id = Guid.CreateVersion7(),
                    CustomerId = customerId,
                    Channel = channel,
                    Purpose = purpose,
                    Status = status,
                    Source = "staff",
                    Evidence = evidence,
                    CapturedAt = nowUtc,
                    CapturedBy = actorId
                };
                db.Add(targetConsent);
            }

            TouchCustomer(customer, expectedVersion, nowUtc);
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);

            var allConsents = await db.Set<Consent>().AsNoTracking()
                .Where(x => x.CustomerId == customerId)
                .OrderBy(x => x.Channel)
                .ThenBy(x => x.Purpose)
                .ThenBy(x => x.Id)
                .Select(x => new CustomerConsentItemView(
                    x.Id, x.Channel, x.Purpose, x.Status, x.Source, x.Evidence, x.CapturedAt, x.CapturedBy))
                .ToArrayAsync(ct);

            var upsertedView = allConsents.Single(x => x.Id == targetConsent.Id);
            return Result.Success(new CustomerConsentsResult(
                customerId,
                CatalogVersions.Encode(customer.Version),
                upsertedView,
                allConsents));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerConsentsResult>(ConcurrencyError());
        }
    }

    public async Task<CustomerMergePairLock> LockPairForMergeAsync(Guid targetCustomerId, Guid sourceCustomerId, CancellationToken ct)
    {
        RequireTransaction();
        _lockedAccountsByCustomer.Clear();
        _mergeChainStubs = [];
        _mergeReferrers = [];

        // Lock the pair strictly in ascending PostgreSQL UUID byte order using FOR NO KEY UPDATE
        // (compatible with PG17 FK FOR KEY SHARE on fk_customers_referred_by_customer_id while still blocking
        // FOR SHARE of EfCustomerOrderResolver, FOR NO KEY UPDATE of concurrent CRM mutations, and FOR UPDATE).
        var targetFirst = string.CompareOrdinal(targetCustomerId.ToString("D"), sourceCustomerId.ToString("D")) <= 0;
        var firstId = targetFirst ? targetCustomerId : sourceCustomerId;
        var secondId = targetFirst ? sourceCustomerId : targetCustomerId;

        var firstRow = await LockCustomerRowForNoKeyUpdateAsync(firstId, ct);
        var secondRow = await LockCustomerRowForNoKeyUpdateAsync(secondId, ct);

        var target = targetFirst ? firstRow : secondRow;
        var source = targetFirst ? secondRow : firstRow;

        if (target is null || source is null)
        {
            return new CustomerMergePairLock(
                target is null ? null : ToRecord(target),
                source is null ? null : ToRecord(source),
                false,
                false);
        }

        _lockedCustomers[targetCustomerId] = target;
        _lockedCustomers[sourceCustomerId] = source;

        if (string.Equals(target.ContactStatus, "erased", StringComparison.Ordinal)
            || string.Equals(source.ContactStatus, "erased", StringComparison.Ordinal)
            || target.MergedIntoCustomerId is not null
            || source.MergedIntoCustomerId is not null)
        {
            return new CustomerMergePairLock(ToRecord(target), ToRecord(source), false, false);
        }

        var pairIds = new[] { firstId, secondId };
        var accounts = await db.Set<CustomerAccount>()
            .FromSqlInterpolated($"SELECT * FROM crm.customer_accounts WHERE customer_id = ANY({pairIds}) ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);

        foreach (var account in accounts)
        {
            _lockedAccountsByCustomer[account.CustomerId] = account;
        }

        var targetHasAccount = _lockedAccountsByCustomer.ContainsKey(targetCustomerId);
        var sourceHasAccount = _lockedAccountsByCustomer.ContainsKey(sourceCustomerId);
        if (targetHasAccount && sourceHasAccount)
        {
            return new CustomerMergePairLock(ToRecord(target), ToRecord(source), true, true);
        }

        // Resolve transitive merged stubs pointing to source (multi-level chain flattening) + customers referred by source.
        var transitiveStubIds = await FindAllTransitiveMergedStubIdsAsync(sourceCustomerId, excludeId: targetCustomerId, ct);
        var referrerIds = await db.Set<Customer>().AsNoTracking()
            .Where(x => x.ReferredByCustomerId == sourceCustomerId
                && x.Id != targetCustomerId
                && x.Id != sourceCustomerId)
            .Select(x => x.Id)
            .ToArrayAsync(ct);

        var auxIds = transitiveStubIds.Concat(referrerIds)
            .Distinct()
            .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        if (auxIds.Length > 0)
        {
            var auxRows = await db.Set<Customer>()
                .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = ANY({auxIds}) ORDER BY id ASC FOR NO KEY UPDATE")
                .ToArrayAsync(ct);
            var auxById = auxRows.ToDictionary(x => x.Id);

            foreach (var stubId in transitiveStubIds)
            {
                if (auxById.TryGetValue(stubId, out var stubRow))
                {
                    _mergeChainStubs.Add(stubRow);
                }
            }

            foreach (var refId in referrerIds.Distinct())
            {
                // Re-verify under row lock so a concurrent PATCH changing referred_by_customer_id away from source is never overwritten.
                if (auxById.TryGetValue(refId, out var refRow) && refRow.ReferredByCustomerId == sourceCustomerId)
                {
                    _mergeReferrers.Add(refRow);
                }
            }
        }

        return new CustomerMergePairLock(ToRecord(target), ToRecord(source), targetHasAccount, sourceHasAccount);
    }

    public async Task<Result<CustomerDetailView>> CompleteMergeAsync(CompleteMergeInput input, CancellationToken ct)
    {
        var tx = RequireTransaction();
        var target = _lockedCustomers[input.TargetCustomerId];
        var source = _lockedCustomers[input.SourceCustomerId];
        _lockedAccountsByCustomer.TryGetValue(input.TargetCustomerId, out var accountA);
        _lockedAccountsByCustomer.TryGetValue(input.SourceCustomerId, out var accountB);

        const string savepoint = "p212_complete_merge";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            // ---- Phase 1: remove collisions & demote duplicate default address BEFORE moving CustomerId (zero 23505) ----
            var tagsA = await db.Set<CustomerTag>()
                .Where(x => x.CustomerId == input.TargetCustomerId)
                .ToArrayAsync(ct);
            var tagsB = await db.Set<CustomerTag>()
                .Where(x => x.CustomerId == input.SourceCustomerId)
                .ToArrayAsync(ct);
            var tagIdsOnA = tagsA.Select(x => x.TagId).ToHashSet();
            var missingTagIds = tagsB.Select(x => x.TagId).Where(id => !tagIdsOnA.Contains(id)).Distinct().ToArray();
            if (tagsB.Length > 0)
            {
                db.RemoveRange(tagsB);
            }

            var addrsA = await db.Set<CustomerAddress>()
                .Where(x => x.CustomerId == input.TargetCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var addrsB = await db.Set<CustomerAddress>()
                .Where(x => x.CustomerId == input.SourceCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var hasAddressesOnA = addrsA.Length > 0;
            if (addrsA.Any(x => x.IsDefault))
            {
                foreach (var addrB in addrsB)
                {
                    if (addrB.IsDefault)
                    {
                        addrB.IsDefault = false;
                        addrB.UpdatedAt = input.NowUtc;
                    }
                }
            }

            var interestsA = await db.Set<CustomerInterest>()
                .Where(x => x.CustomerId == input.TargetCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var interestsB = await db.Set<CustomerInterest>()
                .Where(x => x.CustomerId == input.SourceCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var interestMapA = interestsA.ToDictionary(InterestKey);
            var interestsToMove = new List<CustomerInterest>(interestsB.Length);
            foreach (var rowB in interestsB)
            {
                if (interestMapA.TryGetValue(InterestKey(rowB), out var rowA))
                {
                    rowA.Score = (int)Math.Min((long)rowA.Score + rowB.Score, int.MaxValue);
                    rowA.UpdatedAt = input.NowUtc;
                    db.Remove(rowB);
                }
                else
                {
                    interestsToMove.Add(rowB);
                }
            }

            var consentsA = await db.Set<Consent>()
                .Where(x => x.CustomerId == input.TargetCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var consentsB = await db.Set<Consent>()
                .Where(x => x.CustomerId == input.SourceCustomerId)
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct);
            var consentMapA = consentsA.ToDictionary(c => (c.Channel, c.Purpose));
            var consentsToMove = new List<Consent>(consentsB.Length);
            foreach (var rowB in consentsB)
            {
                if (consentMapA.TryGetValue((rowB.Channel, rowB.Purpose), out var rowA))
                {
                    if (CustomerRules.FirstConsentWins(rowA.CapturedAt, rowA.Status, rowB.CapturedAt, rowB.Status))
                    {
                        db.Remove(rowB);
                    }
                    else
                    {
                        db.Remove(rowA);
                        consentsToMove.Add(rowB);
                    }
                }
                else
                {
                    consentsToMove.Add(rowB);
                }
            }

            // Flush Phase 1 so deleted rows and cleared default flags are visible in PostgreSQL before Phase 2 updates.
            await db.SaveChangesAsync(ct);

            // ---- Phase 2: apply transfers, BR-IAM-04 safeguards, flattening, stats, search_text & audit ----
            foreach (var tagId in missingTagIds)
            {
                db.Add(new CustomerTag { CustomerId = input.TargetCustomerId, TagId = tagId });
            }

            var accountTransferred = false;
            var historyUnlockReset = false;

            if (accountB is not null)
            {
                accountTransferred = true;
                accountB.CustomerId = input.TargetCustomerId;

                var (resetUnlock, resetPhoneVerified) = CustomerRules.ShouldResetMergedAccountUnlock(
                    accountB.LoginPhone,
                    target.PhoneE164,
                    input.TargetHadOrders,
                    hasAddressesOnA);

                if (resetUnlock)
                {
                    historyUnlockReset = true;
                    accountB.HistoryUnlockedAt = null;
                }

                if (resetPhoneVerified)
                {
                    accountB.PhoneVerifiedAt = null;
                }

                // BR-IAM-06: rotate security_stamp whenever an account transfers to invalidate stale sessions.
                accountB.SecurityStamp = Guid.CreateVersion7().ToString("N");

                if (accountB.HistoryUnlockedAt is null)
                {
                    foreach (var addrA in addrsA)
                    {
                        var clamped = CustomerRules.ClampAddressCreatedAtBeforeAccount(addrA.CreatedAt, accountB.CreatedAt);
                        if (clamped != addrA.CreatedAt)
                        {
                            addrA.CreatedAt = clamped;
                            addrA.UpdatedAt = input.NowUtc;
                        }
                    }

                    if (resetPhoneVerified)
                    {
                        foreach (var addrB in addrsB)
                        {
                            var clamped = CustomerRules.ClampAddressCreatedAtBeforeAccount(addrB.CreatedAt, accountB.CreatedAt);
                            if (clamped != addrB.CreatedAt)
                            {
                                addrB.CreatedAt = clamped;
                                addrB.UpdatedAt = input.NowUtc;
                            }
                        }
                    }
                }
            }
            else if (accountA is not null && accountA.HistoryUnlockedAt is null)
            {
                if (string.IsNullOrEmpty(accountA.LoginPhone)
                    || !string.Equals(accountA.LoginPhone, target.PhoneE164, StringComparison.Ordinal))
                {
                    accountA.PhoneVerifiedAt = null;
                }

                accountA.SecurityStamp = Guid.CreateVersion7().ToString("N");

                foreach (var addrB in addrsB)
                {
                    var clamped = CustomerRules.ClampAddressCreatedAtBeforeAccount(addrB.CreatedAt, accountA.CreatedAt);
                    if (clamped != addrB.CreatedAt)
                    {
                        addrB.CreatedAt = clamped;
                        addrB.UpdatedAt = input.NowUtc;
                    }
                }
            }

            foreach (var addrB in addrsB)
            {
                addrB.CustomerId = input.TargetCustomerId;
                addrB.UpdatedAt = input.NowUtc;
            }

            foreach (var interestB in interestsToMove)
            {
                interestB.CustomerId = input.TargetCustomerId;
                interestB.UpdatedAt = input.NowUtc;
            }

            foreach (var consentB in consentsToMove)
            {
                consentB.CustomerId = input.TargetCustomerId;
            }

            var interactionsB = await db.Set<Interaction>()
                .FromSqlInterpolated($"SELECT * FROM crm.interactions WHERE customer_id = {input.SourceCustomerId} ORDER BY id ASC FOR UPDATE")
                .ToArrayAsync(ct);
            foreach (var interaction in interactionsB)
            {
                interaction.CustomerId = input.TargetCustomerId;
            }

            var dataRequestsB = await db.Set<DataRequest>()
                .FromSqlInterpolated($"SELECT * FROM crm.data_requests WHERE customer_id = {input.SourceCustomerId} ORDER BY id ASC FOR UPDATE")
                .ToArrayAsync(ct);
            foreach (var dataRequest in dataRequestsB)
            {
                dataRequest.CustomerId = input.TargetCustomerId;
            }

            // Recompute BR-CRM-07 last_contacted_at across combined A + B non-note interactions.
            var recomputedLastContactedAt = await db.Set<Interaction>().AsNoTracking()
                .Where(x => (x.CustomerId == input.TargetCustomerId || x.CustomerId == input.SourceCustomerId)
                    && x.Kind != InteractionRules.NoteKind)
                .MaxAsync(x => (DateTime?)x.OccurredAt, ct);

            // Flatten multi-level merged chains C -> B into C -> A.
            foreach (var stub in _mergeChainStubs)
            {
                stub.MergedIntoCustomerId = input.TargetCustomerId;
                stub.UpdatedAt = input.NowUtc;
            }

            // Preserve ck_customer_not_self_ref across target, source, and third-party referred customers.
            if (target.ReferredByCustomerId == input.SourceCustomerId)
            {
                target.ReferredByCustomerId = source.ReferredByCustomerId is { } bRef
                    && bRef != input.TargetCustomerId
                    && bRef != input.SourceCustomerId
                    ? bRef
                    : null;
            }

            if (source.ReferredByCustomerId == input.TargetCustomerId)
            {
                source.ReferredByCustomerId = null;
            }

            foreach (var referred in _mergeReferrers)
            {
                referred.ReferredByCustomerId = input.TargetCustomerId;
                referred.UpdatedAt = input.NowUtc;
            }

            // Update source stub B (G17a allows merged_into_customer_id IS NOT NULL with null phone and null zalo_name).
            source.MergedIntoCustomerId = input.TargetCustomerId;
            source.PhoneE164 = null;
            source.FirstDeliveredAt = null;
            source.LastDeliveredAt = null;
            source.DeliveredOrdersCount = 0;
            source.LifetimeValueVnd = 0L;
            source.LastContactedAt = null;
            var sourceSearchSource = CustomerRules.BuildSearchSource(source.FullName, source.ZaloName, source.Code, null, source.Email);
            source.SearchText = await ComputeSearchTextAsync(sourceSearchSource, ct);
            TouchCustomer(source, input.ExpectedSourceVersion, input.NowUtc);

            // Update surviving target A with recomputed real stats and rebuilt search_text.
            target.FirstDeliveredAt = input.DeliveredStats.FirstDeliveredAt;
            target.LastDeliveredAt = input.DeliveredStats.LastDeliveredAt;
            target.DeliveredOrdersCount = input.DeliveredStats.DeliveredOrdersCount;
            target.LifetimeValueVnd = input.DeliveredStats.LifetimeValueVnd;
            target.LastContactedAt = recomputedLastContactedAt;
            var targetSearchSource = CustomerRules.BuildSearchSource(target.FullName, target.ZaloName, target.Code, target.PhoneE164, target.Email);
            target.SearchText = await ComputeSearchTextAsync(targetSearchSource, ct);
            TouchCustomer(target, input.ExpectedTargetVersion, input.NowUtc);

            auditWriter.RecordMerge(new CustomerMergeAudit(
                input.TargetCustomerId,
                input.SourceCustomerId,
                _mergeChainStubs.Count,
                accountTransferred,
                historyUnlockReset,
                input.MovedOrdersCount,
                interactionsB.Length,
                input.MovedVouchersCount,
                input.MovedRedemptionsCount));

            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerDetailView>(ConcurrencyError());
        }

        var detail = await queries.GetDetailAsync(input.TargetCustomerId, ct);
        return detail is null
            ? throw new InvalidOperationException("Merged target customer could not be reloaded.")
            : Result.Success(detail);
    }

    public async Task<CustomerEraseLock> LockForEraseAsync(Guid customerId, CancellationToken ct)
    {
        RequireTransaction();
        _eraseMergedStubs = [];
        _eraseAccounts = [];
        _eraseAllCustomerIds = [];

        var root = await LockCustomerRowForNoKeyUpdateAsync(customerId, ct);
        if (root is null)
        {
            return new CustomerEraseLock(null, [], []);
        }

        _lockedCustomers[customerId] = root;
        if (root.MergedIntoCustomerId is not null
            || string.Equals(root.ContactStatus, "erased", StringComparison.Ordinal))
        {
            return new CustomerEraseLock(ToRecord(root), [customerId], []);
        }

        // ASSUMPTION-P212-03 = A + Multi-Level Merged Stubs: iteratively find ALL stubs pointing transitively
        // to root at any depth (D -> C -> B -> A) and lock them in ascending PostgreSQL UUID order.
        var stubIds = await FindAllTransitiveMergedStubIdsAsync(customerId, excludeId: null, ct);
        if (stubIds.Length > 0)
        {
            var stubRows = await db.Set<Customer>()
                .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = ANY({stubIds}) ORDER BY id ASC FOR NO KEY UPDATE")
                .ToArrayAsync(ct);
            _eraseMergedStubs.AddRange(stubRows);
        }

        _eraseAllCustomerIds = [customerId, .. stubIds];

        var accounts = await db.Set<CustomerAccount>()
            .FromSqlInterpolated($"SELECT * FROM crm.customer_accounts WHERE customer_id = ANY({_eraseAllCustomerIds}) ORDER BY id ASC FOR UPDATE")
            .ToArrayAsync(ct);
        _eraseAccounts.AddRange(accounts);

        return new CustomerEraseLock(
            ToRecord(root),
            _eraseAllCustomerIds,
            accounts.Select(x => x.Id).ToArray());
    }

    public async Task<Result<CustomerEraseResult>> CompleteEraseAsync(CompleteEraseInput input, CancellationToken ct)
    {
        var tx = RequireTransaction();
        var root = _lockedCustomers[input.CustomerId];
        var allCustomerIds = _eraseAllCustomerIds;

        const string savepoint = "p212_complete_erase";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            // E1: delete addresses, tags, interests, consents across root and any merged stubs.
            var addresses = await db.Set<CustomerAddress>()
                .Where(x => allCustomerIds.Contains(x.CustomerId))
                .ToArrayAsync(ct);
            if (addresses.Length > 0) db.RemoveRange(addresses);

            var tags = await db.Set<CustomerTag>()
                .Where(x => allCustomerIds.Contains(x.CustomerId))
                .ToArrayAsync(ct);
            if (tags.Length > 0) db.RemoveRange(tags);

            var interests = await db.Set<CustomerInterest>()
                .Where(x => allCustomerIds.Contains(x.CustomerId))
                .ToArrayAsync(ct);
            if (interests.Length > 0) db.RemoveRange(interests);

            var consents = await db.Set<Consent>()
                .Where(x => allCustomerIds.Contains(x.CustomerId))
                .ToArrayAsync(ct);
            if (consents.Length > 0) db.RemoveRange(consents);

            // E2 (R-P204-01 / BR-CRM-11): scrub interaction content and follow_up_result, closing ONLY open follow-ups.
            var interactions = await db.Set<Interaction>()
                .FromSqlInterpolated($"SELECT * FROM crm.interactions WHERE customer_id = ANY({allCustomerIds}) ORDER BY id ASC FOR UPDATE")
                .ToArrayAsync(ct);
            foreach (var interaction in interactions)
            {
                var (content, followUpResult, followUpDoneAt) = CustomerRules.ComputeInteractionScrubState(
                    interaction.FollowUpAt,
                    interaction.FollowUpDoneAt,
                    input.NowUtc);
                interaction.Content = content;
                interaction.FollowUpResult = followUpResult;
                interaction.FollowUpDoneAt = followUpDoneAt;
            }

            // E3: scrub notes on prior data_requests, close any open requests, and append the completed erase request.
            var priorRequests = await db.Set<DataRequest>()
                .FromSqlInterpolated($"SELECT * FROM crm.data_requests WHERE customer_id = ANY({allCustomerIds}) ORDER BY id ASC FOR UPDATE")
                .ToArrayAsync(ct);
            foreach (var request in priorRequests)
            {
                request.Note = null;
                if (string.Equals(request.Status, "open", StringComparison.Ordinal))
                {
                    request.Status = "completed";
                    request.CompletedAt = input.NowUtc;
                    request.HandledBy = input.ActorId;
                }
            }

            var eraseRequestId = Guid.CreateVersion7();
            db.Add(new DataRequest
            {
                Id = eraseRequestId,
                CustomerId = input.CustomerId,
                RequestType = "erase",
                Status = "completed",
                Channel = input.RequestChannel,
                Note = null,
                RequestedAt = input.NowUtc,
                CompletedAt = input.NowUtc,
                HandledBy = input.ActorId
            });

            // E4: delete external logins and disable customer account(s), rotating security_stamp (BR-IAM-06).
            var accountDisabled = false;
            if (_eraseAccounts.Count > 0)
            {
                var accountIds = _eraseAccounts.Select(x => x.Id).ToArray();
                var logins = await db.Set<CustomerLogin>()
                    .Where(x => accountIds.Contains(x.AccountId))
                    .ToArrayAsync(ct);
                if (logins.Length > 0) db.RemoveRange(logins);

                foreach (var account in _eraseAccounts)
                {
                    account.Status = "disabled";
                    account.LoginPhone = null;
                    account.Email = null;
                    account.EmailVerifiedAt = null;
                    account.PhoneVerifiedAt = null;
                    account.HistoryUnlockedAt = null;
                    account.PasswordHash = null;
                    account.FailedLoginCount = 0;
                    account.LockoutEnd = null;
                    account.SecurityStamp = Guid.CreateVersion7().ToString("N");
                    accountDisabled = true;
                }
            }

            // E6: anonymize root customer and ALL transitive merged stubs (ASSUMPTION-P212-03 = A).
            AnonymizeCustomerRow(root, input.ExpectedVersion, input.NowUtc);

            var scrubbedStubsCount = 0;
            foreach (var stub in _eraseMergedStubs)
            {
                if (!string.Equals(stub.ContactStatus, "erased", StringComparison.Ordinal))
                {
                    AnonymizeCustomerRow(stub, expectedVersion: null, input.NowUtc);
                    scrubbedStubsCount++;
                }
            }

            auditWriter.RecordErase(new CustomerEraseAudit(
                input.CustomerId,
                eraseRequestId,
                input.RequestChannel,
                input.AnonymizedOrdersCount,
                interactions.Length,
                scrubbedStubsCount,
                accountDisabled,
                input.Reason));

            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);

            return Result.Success(new CustomerEraseResult(
                root.Id,
                root.Code,
                root.ContactStatus,
                root.ErasedAt!.Value,
                CatalogVersions.Encode(root.Version),
                eraseRequestId,
                input.AnonymizedOrdersCount,
                scrubbedStubsCount));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerEraseResult>(ConcurrencyError());
        }
    }

    public async Task<CustomerCrmExportSnapshot?> LoadForExportAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == customerId, ct);
        if (customer is null)
        {
            return null;
        }

        var tags = await (
            from ctRow in db.Set<CustomerTag>().AsNoTracking()
            join t in db.Set<Tag>().AsNoTracking() on ctRow.TagId equals t.Id
            where ctRow.CustomerId == customerId
            orderby t.Name, t.Id
            select new CustomerTagView(t.Id, t.Name, t.Color))
            .ToArrayAsync(ct);

        var addresses = await queries.ListAddressesAsync(customerId, ct);

        CustomerExportAccountView? accountView = null;
        var account = await db.Set<CustomerAccount>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CustomerId == customerId, ct);
        if (account is not null)
        {
            var logins = await db.Set<CustomerLogin>().AsNoTracking()
                .Where(x => x.AccountId == account.Id)
                .OrderBy(x => x.Provider)
                .ThenBy(x => x.CreatedAt)
                .Select(x => new CustomerExportLoginView(x.Provider, x.Email, x.CreatedAt))
                .ToArrayAsync(ct);

            accountView = new CustomerExportAccountView(
                account.Id,
                account.LoginPhone,
                account.Email,
                account.EmailVerifiedAt,
                account.PhoneVerifiedAt,
                account.HistoryUnlockedAt,
                account.Status,
                account.CreatedAt,
                account.LastLoginAt,
                logins);
        }

        var consents = await db.Set<Consent>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderBy(x => x.Channel)
            .ThenBy(x => x.Purpose)
            .ThenBy(x => x.Id)
            .Select(x => new CustomerConsentItemView(
                x.Id, x.Channel, x.Purpose, x.Status, x.Source, x.Evidence, x.CapturedAt, x.CapturedBy))
            .ToArrayAsync(ct);

        var interests = await db.Set<CustomerInterest>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new CustomerInterestView(x.Id, x.InterestType, x.RefId, x.PriceBand, x.Source, x.Score, x.UpdatedAt))
            .ToArrayAsync(ct);

        var interactions = await db.Set<Interaction>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new CustomerExportInteractionView(
                x.Id,
                x.Channel,
                x.Kind,
                x.Content,
                x.OccurredAt,
                x.OrderId,
                x.FollowUpAt,
                x.FollowUpDoneAt,
                x.FollowUpResult,
                x.CreatedAt))
            .ToArrayAsync(ct);

        var dataRequests = await db.Set<DataRequest>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.RequestedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new CustomerExportDataRequestView(
                x.Id,
                x.RequestType,
                x.Status,
                x.Channel,
                x.Note,
                x.RequestedAt,
                x.CompletedAt))
            .ToArrayAsync(ct);

        var profile = new CustomerExportProfileView(
            customer.Id,
            customer.Code,
            customer.FullName,
            customer.PhoneE164,
            customer.Email,
            customer.ZaloName,
            customer.CustomerGroup,
            customer.IsVip,
            customer.Origin,
            customer.AcquisitionSource,
            customer.ReferredByCustomerId,
            customer.ContactStatus,
            customer.NoteSummary,
            customer.HadLegacyPurchase,
            customer.DeliveredOrdersCount,
            customer.LifetimeValueVnd,
            customer.FirstDeliveredAt,
            customer.LastDeliveredAt,
            customer.LastContactedAt,
            customer.MergedIntoCustomerId,
            customer.ErasedAt,
            customer.CreatedAt,
            customer.UpdatedAt);

        return new CustomerCrmExportSnapshot(
            profile,
            tags,
            addresses,
            accountView,
            consents,
            interests,
            interactions,
            dataRequests);
    }

    public async Task RecordExportAccessAuditAsync(Guid customerId, CancellationToken ct)
    {
        RequireTransaction();
        auditWriter.RecordExportAccess(customerId);
        await db.SaveChangesAsync(ct);
    }

    // Merge/Erase never DELETE crm.customers or alter PK id; FOR NO KEY UPDATE stays compatible with PG17 FK FOR KEY SHARE
    // (e.g., fk_customers_referred_by_customer_id) while blocking FOR SHARE, FOR NO KEY UPDATE, and FOR UPDATE.
    private async Task<Customer?> LockCustomerRowForNoKeyUpdateAsync(Guid id, CancellationToken ct)
        => (await db.Set<Customer>()
            .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = {id} FOR NO KEY UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();

    private async Task<Guid[]> FindAllTransitiveMergedStubIdsAsync(Guid rootId, Guid? excludeId, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        var frontier = new[] { rootId };

        while (frontier.Length > 0)
        {
            var nextBatch = await db.Set<Customer>().AsNoTracking()
                .Where(x => x.MergedIntoCustomerId != null && frontier.Contains(x.MergedIntoCustomerId.Value))
                .Select(x => x.Id)
                .ToArrayAsync(ct);

            var newlyDiscovered = new List<Guid>(nextBatch.Length);
            foreach (var candidateId in nextBatch)
            {
                if (candidateId == rootId || (excludeId is { } excluded && candidateId == excluded))
                {
                    continue;
                }

                if (found.Add(candidateId))
                {
                    newlyDiscovered.Add(candidateId);
                }
            }

            frontier = [.. newlyDiscovered];
        }

        return found
            .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)
            .ToArray();
    }

    private void AnonymizeCustomerRow(Customer customer, uint? expectedVersion, DateTime nowUtc)
    {
        customer.ContactStatus = "erased";
        customer.ErasedAt = nowUtc;
        customer.FullName = CustomerRules.AnonymizedText;
        customer.PhoneE164 = null;
        customer.Email = null;
        customer.ZaloName = null;
        customer.NoteSummary = null;
        customer.SearchText = "";
        customer.LastContactedAt = null;
        TouchCustomer(customer, expectedVersion, nowUtc);
        db.Entry(customer).Property(x => x.SearchText).IsModified = true;
    }

    private void TouchCustomer(Customer customer, uint? expectedVersion, DateTime nowUtc)
    {
        customer.UpdatedAt = nowUtc;
        var entry = db.Entry(customer);
        if (expectedVersion is { } ev)
        {
            entry.Property(x => x.Version).OriginalValue = ev;
        }

        entry.Property(x => x.UpdatedAt).IsModified = true;
    }

    private Task<string> ComputeSearchTextAsync(string source, CancellationToken ct)
        => db.Database
            .SqlQuery<string>($"SELECT public.f_unaccent({source}) AS \"Value\"")
            .SingleAsync(ct);

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction RequireTransaction()
        => db.Database.CurrentTransaction
           ?? throw new InvalidOperationException("Customer privacy operations require an active command transaction.");

    private static (string InterestType, Guid RefId, string PriceBand, string Source) InterestKey(CustomerInterest row)
        => (row.InterestType, row.RefId ?? Guid.Empty, row.PriceBand ?? "", row.Source);

    private static Error ConcurrencyError()
        => new("concurrency_conflict", "Dữ liệu khách hàng đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);

    private static CustomerRecord ToRecord(Customer row) => new()
    {
        Id = row.Id,
        Code = row.Code,
        FullName = row.FullName,
        PhoneE164 = row.PhoneE164,
        Email = row.Email,
        ZaloName = row.ZaloName,
        CustomerGroup = row.CustomerGroup,
        IsVip = row.IsVip,
        Origin = row.Origin,
        AcquisitionSource = row.AcquisitionSource,
        ReferredByCustomerId = row.ReferredByCustomerId,
        ContactStatus = row.ContactStatus,
        NoteSummary = row.NoteSummary,
        HadLegacyPurchase = row.HadLegacyPurchase,
        FirstDeliveredAt = row.FirstDeliveredAt,
        LastDeliveredAt = row.LastDeliveredAt,
        DeliveredOrdersCount = row.DeliveredOrdersCount,
        LifetimeValueVnd = row.LifetimeValueVnd,
        LastContactedAt = row.LastContactedAt,
        SearchText = row.SearchText,
        MergedIntoCustomerId = row.MergedIntoCustomerId,
        ErasedAt = row.ErasedAt,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        Version = row.Version
    };
}
