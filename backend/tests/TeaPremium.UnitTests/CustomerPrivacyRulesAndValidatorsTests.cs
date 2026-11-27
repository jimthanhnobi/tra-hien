using System.Text;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Pricing.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Crm;
using TeaPremium.Domain.Orders;

namespace TeaPremium.UnitTests;

public sealed class CustomerPrivacyRulesAndValidatorsTests
{
    private static readonly CustomerActor OwnerActor = new(
        Guid.CreateVersion7(),
        ["customers.read", "customers.write", "customers.merge", "customers.erase", "customers.export"],
        true);

    private static readonly string ValidRowVersion = CatalogVersions.Encode(77u);
    private static readonly DateTime FixedNowUtc = new(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("invalid_channel", "marketing_new_products", "granted", "Khách đồng ý qua Zalo", null)]
    [InlineData("zalo_personal", "invalid_purpose", "granted", "Khách đồng ý qua Zalo", null)]
    [InlineData("zalo_personal", "marketing_new_products", "pending", "Khách đồng ý qua Zalo", null)]
    [InlineData("zalo_personal", "marketing_new_products", "granted", "ab", null)]
    [InlineData("zalo_personal", "marketing_new_products", "granted", "   a  ", null)]
    [InlineData("zalo_personal", "marketing_new_products", "granted", "Zalo\0Bad", null)]
    [InlineData("zalo_personal", "marketing_new_products", "granted", "Khách đồng ý qua Zalo", "not-base64")]
    [InlineData("zalo_personal", "marketing_new_products", "granted", "Khách đồng ý qua Zalo", "")]
    public void BrCrm09PutCustomerConsentValidatorEnforcesChannelPurposeStatusTrimmedEvidenceAndRowVersion(
        string channel,
        string purpose,
        string status,
        string evidence,
        string? rowVersion)
    {
        var validator = new PutCustomerConsentValidator();
        var cmd = new PutCustomerConsent(Guid.CreateVersion7(), channel, purpose, status, evidence, rowVersion, OwnerActor);
        Assert.False(validator.Validate(cmd).IsValid);

        var tooLong = new PutCustomerConsent(
            Guid.CreateVersion7(), "zalo_personal", "customer_care", "revoked", new string('x', 1001), null, OwnerActor);
        Assert.False(validator.Validate(tooLong).IsValid);

        var validOmittedRv = new PutCustomerConsent(
            Guid.CreateVersion7(), "zalo_personal", "marketing_new_products", "granted", "  Khách đồng ý qua Zalo  ", null, OwnerActor);
        Assert.True(validator.Validate(validOmittedRv).IsValid);

        var validWithRv = new PutCustomerConsent(
            Guid.CreateVersion7(), "email", "marketing_promotions", "revoked", "Khách nhắn ngừng nhận tin", ValidRowVersion, OwnerActor);
        Assert.True(validator.Validate(validWithRv).IsValid);
    }

    [Fact]
    public void BrCrm05Rp20105MergeCustomersValidatorRejectsSelfMergeEmptyIdsAndMalformedRowVersion()
    {
        var validator = new MergeCustomersValidator();
        var idA = Guid.CreateVersion7();
        var idB = Guid.CreateVersion7();

        var selfMerge = validator.Validate(new MergeCustomers(idA, idA, null, null, OwnerActor));
        Assert.False(selfMerge.IsValid);
        Assert.Contains(selfMerge.Errors, e => e.PropertyName == "mergeCustomerId" && e.ErrorMessage == "self_merge_not_allowed");

        Assert.False(validator.Validate(new MergeCustomers(Guid.Empty, idB, null, null, OwnerActor)).IsValid);
        Assert.False(validator.Validate(new MergeCustomers(idA, Guid.Empty, null, null, OwnerActor)).IsValid);
        Assert.False(validator.Validate(new MergeCustomers(idA, idB, "bad", null, OwnerActor)).IsValid);
        Assert.False(validator.Validate(new MergeCustomers(idA, idB, null, "bad", OwnerActor)).IsValid);

        Assert.True(validator.Validate(new MergeCustomers(idA, idB, null, null, OwnerActor)).IsValid);
        Assert.True(validator.Validate(new MergeCustomers(idA, idB, ValidRowVersion, ValidRowVersion, OwnerActor)).IsValid);
    }

    [Theory]
    [InlineData("ab", "zalo_personal", null)]
    [InlineData("   x  ", "zalo_personal", null)]
    [InlineData("Lý do\0lỗi", "zalo_personal", null)]
    [InlineData("Khách yêu cầu xóa dữ liệu", "telegram", null)]
    [InlineData("Khách yêu cầu xóa dữ liệu", "zalo_personal", "bad-rv")]
    public void BrCrm11EraseCustomerValidatorEnforcesReasonLengthRequestChannelAllowlistAndNulRejection(
        string reason,
        string requestChannel,
        string? rowVersion)
    {
        var validator = new EraseCustomerValidator();
        var cmd = new EraseCustomer(Guid.CreateVersion7(), reason, requestChannel, rowVersion, OwnerActor);
        Assert.False(validator.Validate(cmd).IsValid);

        var tooLongReason = new EraseCustomer(Guid.CreateVersion7(), new string('r', 501), "phone", null, OwnerActor);
        Assert.False(validator.Validate(tooLongReason).IsValid);

        foreach (var allowedChannel in CustomerRules.DataRequestChannels)
        {
            var valid = new EraseCustomer(Guid.CreateVersion7(), "Khách gọi xác nhận yêu cầu xóa dữ liệu", allowedChannel, ValidRowVersion, OwnerActor);
            Assert.True(validator.Validate(valid).IsValid);
        }
    }

    [Fact]
    public void BrCrm13ExportCustomerDataValidatorRejectsEmptyCustomerId()
    {
        var validator = new ExportCustomerDataValidator();
        Assert.False(validator.Validate(new ExportCustomerData(Guid.Empty, OwnerActor)).IsValid);
        Assert.False(validator.Validate(new ExportCustomerData(Guid.CreateVersion7(), new CustomerActor(Guid.Empty, [], true))).IsValid);
        Assert.True(validator.Validate(new ExportCustomerData(Guid.CreateVersion7(), OwnerActor)).IsValid);
    }

    [Theory]
    [InlineData(10, "granted", 20, "revoked", false)]
    [InlineData(20, "granted", 10, "revoked", true)]
    [InlineData(15, "revoked", 15, "granted", true)]
    [InlineData(15, "granted", 15, "revoked", false)]
    [InlineData(15, "granted", 15, "granted", true)]
    [InlineData(15, "revoked", 15, "revoked", true)]
    public void BrCrm05ConsentWinnerSelectionKeepsLatestCapturedAtAndResolvesTimestampTieSafely(
        int firstSeconds,
        string firstStatus,
        int secondSeconds,
        string secondStatus,
        bool expectedFirstWins)
    {
        var baseTime = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var actual = CustomerRules.FirstConsentWins(
            baseTime.AddSeconds(firstSeconds),
            firstStatus,
            baseTime.AddSeconds(secondSeconds),
            secondStatus);
        Assert.Equal(expectedFirstWins, actual);
    }

    [Theory]
    [InlineData("+84901234567", "+84901234567", false, false, false, false)]
    [InlineData("+84901234567", "+84901234567", true, false, true, false)]
    [InlineData("+84901234567", "+84901234567", false, true, true, false)]
    [InlineData("+84901234567", "+84909999999", false, false, true, true)]
    [InlineData("+84901234567", null, false, false, true, true)]
    [InlineData(null, null, false, false, true, true)]
    public void BrIam04AccountMergePolicyResetsUnlockAndRotatesStampWhenPhoneDiffersOrTargetHasUnverifiedHistory(
        string? accountLoginPhone,
        string? targetPhone,
        bool targetHasOrders,
        bool targetHasAddresses,
        bool expectedResetUnlock,
        bool expectedResetPhoneVerified)
    {
        var (resetUnlock, resetPhoneVerified) = CustomerRules.ShouldResetMergedAccountUnlock(
            accountLoginPhone,
            targetPhone,
            targetHasOrders,
            targetHasAddresses);

        Assert.Equal(expectedResetUnlock, resetUnlock);
        Assert.Equal(expectedResetPhoneVerified, resetPhoneVerified);
    }

    [Fact]
    public void BrIam04ClampAddressCreatedAtBeforeAccountSubtractsOneMicrosecondForPostgresTimestamptz()
    {
        var accountCreatedAt = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddTicks(123_456_7);
        var expectedTruncatedAccount = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddTicks(123_456_0);
        var expectedClamped = expectedTruncatedAccount.AddMicroseconds(-1);

        // Address created strictly after account -> clamped to trunc_us(account) - 1us.
        Assert.Equal(expectedClamped, CustomerRules.ClampAddressCreatedAtBeforeAccount(accountCreatedAt.AddMinutes(5), accountCreatedAt));

        // Address created within the exact same microsecond (sub-microsecond ticks) -> still clamped strictly below PG microsecond boundary!
        var sameMicroAddress = expectedTruncatedAccount.AddTicks(2);
        var clampedSameMicro = CustomerRules.ClampAddressCreatedAtBeforeAccount(sameMicroAddress, accountCreatedAt);
        Assert.Equal(expectedClamped, clampedSameMicro);
        Assert.True(clampedSameMicro < expectedTruncatedAccount);
        Assert.Equal(0, clampedSameMicro.Ticks % TimeSpan.TicksPerMicrosecond);

        // Address already earlier than account -> unchanged.
        var olderAddress = expectedTruncatedAccount.AddSeconds(-30);
        Assert.Equal(olderAddress, CustomerRules.ClampAddressCreatedAtBeforeAccount(olderAddress, accountCreatedAt));
    }

    [Fact]
    public void AssumptionP21203ResolveMergedStubClosureFindsMultiLevelChainsAndHandlesCyclesSafely()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        var d = Guid.CreateVersion7();
        var e = Guid.CreateVersion7();
        var unrelated = Guid.CreateVersion7();
        var cycle1 = Guid.CreateVersion7();
        var cycle2 = Guid.CreateVersion7();

        var links = new (Guid Id, Guid? MergedIntoCustomerId)[]
        {
            (a, null),
            (b, a),
            (c, b),
            (d, c),
            (e, b),
            (unrelated, null),
            (cycle1, cycle2),
            (cycle2, cycle1)
        };

        var closure = CustomerRules.ResolveMergedStubClosure(a, links);
        var expected = new[] { b, c, d, e }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, closure);
    }

    [Fact]
    public void BrOrd072BrRet04O3bDeliveredOrderStatsCalculationUsesCommercialRefundsOnlyNeverOverpaymentAndRequires100PctRefundReturn()
    {
        var t1 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        var t4 = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        var t5 = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
        var t6 = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

        // Order 1: delivered with OVERPAYMENT refund of 500k -> overpayment MUST NOT reduce LTV or exclude order!
        var o1 = Guid.CreateVersion7();
        var o1Line = Guid.CreateVersion7();

        // Order 2: delivered (total 1_030_000) with partial return refund (300k) + adjustment (100k) -> LTV = 630_000, stays counted.
        var o2 = Guid.CreateVersion7();
        var o2Line1 = Guid.CreateVersion7();
        var o2Line2 = Guid.CreateVersion7();

        // Order 3: delivered (subtotal 1_000_000, discount 100_000, shipping 30_000, total 930_000):
        // 100% of all lines returned across TWO resolved resolution='refund' requests AND commercial goods refund == 900_000 -> EXCLUDED (BR-RET-04)!
        var o3 = Guid.CreateVersion7();
        var o3Line1 = Guid.CreateVersion7();
        var o3Line2 = Guid.CreateVersion7();

        // Order 4: delivered, 100% returned via REPLACEMENT (NOT refund) -> STAYS COUNTED (never excluded by replacement)!
        var o4 = Guid.CreateVersion7();
        var o4Line = Guid.CreateVersion7();

        // Order 5: delivered, 100% returned via resolved resolution='refund' BUT commercial goods refund < (subtotal - discount) -> STAYS COUNTED with net LTV!
        var o5 = Guid.CreateVersion7();
        var o5Line = Guid.CreateVersion7();

        // Order 6: backfilled delivered order with 0 lines (legacy history) -> INCLUDED (BR-ORD-09)!
        var o6 = Guid.CreateVersion7();

        var orders = new DeliveredOrderStatsOrderInput[]
        {
            new(o1, 1_000_000L, 0L, 1_000_000L, t1),
            new(o2, 1_000_000L, 0L, 1_030_000L, t2),
            new(o3, 1_000_000L, 100_000L, 930_000L, t3),
            new(o4, 500_000L, 0L, 500_000L, t4),
            new(o5, 800_000L, 0L, 800_000L, t5),
            new(o6, 2_000_000L, 0L, 2_000_000L, t6)
        };

        var lines = new DeliveredOrderStatsLineInput[]
        {
            new(o1Line, o1, 1),
            new(o2Line1, o2, 1),
            new(o2Line2, o2, 2),
            new(o3Line1, o3, 2),
            new(o3Line2, o3, 1),
            new(o4Line, o4, 1),
            new(o5Line, o5, 2)
        };

        var refunds = new DeliveredOrderStatsRefundInput[]
        {
            // Overpayment refund on O1 must be ignored completely!
            new(o1, RefundKind.Overpayment, 500_000L, 0L),
            // Partial commercial refunds on O2: 300k return + 100k adjustment = 400k total.
            new(o2, RefundKind.Return, 300_000L, 0L),
            new(o2, RefundKind.Adjustment, 100_000L, 0L),
            // Full commercial goods refund on O3: 600k + 300k = 900k goods (== 1_000_000 - 100_000).
            new(o3, RefundKind.Return, 600_000L, 0L),
            new(o3, RefundKind.Return, 300_000L, 0L),
            // Partial commercial refund on O5: only 500k goods refunded (< 800k), plus 300k overpayment (ignored!).
            new(o5, RefundKind.Return, 500_000L, 0L),
            new(o5, RefundKind.Overpayment, 300_000L, 0L)
        };

        var returnLines = new DeliveredOrderStatsReturnLineInput[]
        {
            // O2: only line 1 returned (line 2 still kept).
            new(o2, o2Line1, 1, ReturnRequestStatus.Resolved, ReturnResolution.Refund),
            // O3: split across two resolved refund return requests covering 100% of line1 (1+1=2) and line2 (1).
            new(o3, o3Line1, 1, ReturnRequestStatus.Resolved, ReturnResolution.Refund),
            new(o3, o3Line1, 1, ReturnRequestStatus.Resolved, ReturnResolution.Refund),
            new(o3, o3Line2, 1, ReturnRequestStatus.Resolved, ReturnResolution.Refund),
            // O4: 100% replacement must NOT trigger BR-RET-04 exclusion.
            new(o4, o4Line, 1, ReturnRequestStatus.Resolved, ReturnResolution.Replacement),
            // O5: 100% returned via refund return request, but goods refund was only 500k < 800k.
            new(o5, o5Line, 2, ReturnRequestStatus.Resolved, ReturnResolution.Refund)
        };

        var stats = CustomerOrderStatsCalculator.Compute(orders, lines, refunds, returnLines);

        // O1 (1_000_000) + O2 (630_000) + O4 (500_000) + O5 (300_000) + O6 (2_000_000) = 4_430_000; O3 excluded!
        Assert.Equal(5, stats.DeliveredOrdersCount);
        Assert.Equal(4_430_000L, stats.LifetimeValueVnd);
        Assert.Equal(t1, stats.FirstDeliveredAt);
        Assert.Equal(t6, stats.LastDeliveredAt);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void BrCrm11Rp20401InteractionScrubRuleClearsFollowUpResultAndClosesOnlyOpenFollowUps(
        bool hasFollowUpAt,
        bool hasExistingDoneAt,
        bool expectDoneAtSetToNow)
    {
        DateTime? followUpAt = hasFollowUpAt ? FixedNowUtc.AddDays(-1) : null;
        var existingDone = FixedNowUtc.AddHours(-2);
        DateTime? followUpDoneAt = hasExistingDoneAt ? existingDone : null;

        var (content, result, nextDoneAt) = CustomerRules.ComputeInteractionScrubState(followUpAt, followUpDoneAt, FixedNowUtc);

        Assert.Equal(CustomerRules.AnonymizedText, content);
        Assert.Null(result);

        if (!hasFollowUpAt)
        {
            Assert.Null(nextDoneAt);
        }
        else if (expectDoneAtSetToNow)
        {
            Assert.Equal(FixedNowUtc, nextDoneAt);
        }
        else
        {
            Assert.Equal(existingDone, nextDoneAt);
        }

        // Verify PostgreSQL ck_interactions_row: follow_up_done_at IS NULL OR follow_up_at IS NOT NULL.
        Assert.True(nextDoneAt is null || followUpAt is not null);
    }

    [Theory]
    [InlineData("erased_target", "customer_erased")]
    [InlineData("erased_source", "customer_erased")]
    [InlineData("merged_target", "customer_merged")]
    [InlineData("merged_source", "customer_merged")]
    [InlineData("dual_accounts", "account_exists")]
    [InlineData("stale_target_version", "concurrency_conflict")]
    public async Task BrCrm05MergeCustomersHandlerRejectsSelfErasedMergedStaleOrDualAccountBeforeMutatingPorts(
        string scenario,
        string expectedErrorCode)
    {
        var idA = Guid.CreateVersion7();
        var idB = Guid.CreateVersion7();
        var target = new CustomerRecord { Id = idA, Code = "KH001000", FullName = "A", ContactStatus = "active", Version = 10u };
        var source = new CustomerRecord { Id = idB, Code = "KH001001", FullName = "B", ContactStatus = "active", Version = 20u };
        var targetHasAccount = false;
        var sourceHasAccount = false;
        string? rowVersion = null;

        switch (scenario)
        {
            case "erased_target": target.ContactStatus = "erased"; break;
            case "erased_source": source.ContactStatus = "erased"; break;
            case "merged_target": target.MergedIntoCustomerId = Guid.CreateVersion7(); break;
            case "merged_source": source.MergedIntoCustomerId = Guid.CreateVersion7(); break;
            case "dual_accounts": targetHasAccount = true; sourceHasAccount = true; break;
            case "stale_target_version": rowVersion = CatalogVersions.Encode(999u); break;
        }

        var store = new FakePrivacyStore
        {
            PairLockResult = new CustomerMergePairLock(target, source, targetHasAccount, sourceHasAccount)
        };
        var orders = new FakeOrdersPort();
        var pricing = new FakePricingPort();
        var handler = new MergeCustomersHandler(store, orders, pricing, new FixedClock(FixedNowUtc));

        var result = await handler.Handle(new MergeCustomers(idA, idB, rowVersion, null, OwnerActor), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedErrorCode, result.Error.Code);
        Assert.Equal(0, orders.ReassignCalls);
        Assert.Equal(0, pricing.ReassignCalls);
        Assert.Equal(0, store.CompleteMergeCalls);
    }

    [Theory]
    [InlineData("not_found", "not_found")]
    [InlineData("merged_stub", "customer_merged")]
    [InlineData("already_erased", "customer_erased")]
    [InlineData("stale_version", "concurrency_conflict")]
    [InlineData("sales_blockers", "customer_erase_blocked")]
    public async Task BrCrm11EraseCustomerHandlerRejectsMergedStubErasedStaleAndPropagatesSalesBlockersWithoutMutating(
        string scenario,
        string expectedErrorCode)
    {
        var customerId = Guid.CreateVersion7();
        var mergedTargetId = Guid.CreateVersion7();
        CustomerRecord? record = new() { Id = customerId, Code = "KH001000", FullName = "Khách", ContactStatus = "active", Version = 5u };
        string? rowVersion = null;
        string[] blockers = [];

        switch (scenario)
        {
            case "not_found": record = null; break;
            case "merged_stub": record.MergedIntoCustomerId = mergedTargetId; break;
            case "already_erased": record.ContactStatus = "erased"; break;
            case "stale_version": rowVersion = CatalogVersions.Encode(999u); break;
            case "sales_blockers": blockers = ["active_orders", "awaiting_cod_settlement"]; break;
        }

        var store = new FakePrivacyStore
        {
            EraseLockResult = new CustomerEraseLock(record, record is null ? [] : [customerId], [])
        };
        var orders = new FakeOrdersPort { EraseBlockers = blockers };
        var handler = new EraseCustomerHandler(store, orders, new FixedClock(FixedNowUtc));

        var result = await handler.Handle(
            new EraseCustomer(customerId, "Khách yêu cầu xóa dữ liệu", "zalo_personal", rowVersion, OwnerActor),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedErrorCode, result.Error.Code);
        Assert.Equal(0, orders.DeleteCartsCalls);
        Assert.Equal(0, orders.AnonymizeOrdersCalls);
        Assert.Equal(0, store.CompleteEraseCalls);

        if (scenario == "merged_stub")
        {
            Assert.NotNull(result.Error.Data);
            Assert.Equal(mergedTargetId, result.Error.Data!["mergedIntoCustomerId"]);
        }
        else if (scenario == "sales_blockers")
        {
            Assert.NotNull(result.Error.Data);
            Assert.Equal(blockers, (IReadOnlyList<string>)result.Error.Data!["blockingReasons"]!);
        }
    }

    [Fact]
    public async Task BrCrm13ExportCustomerDataHandlerReturns404WithoutAuditWhenMissingAndRecordsAuditOnSuccess()
    {
        var missingId = Guid.CreateVersion7();
        var storeMissing = new FakePrivacyStore { ExportSnapshot = null };
        var ordersMissing = new FakeOrdersPort();
        var handlerMissing = new ExportCustomerDataHandler(storeMissing, ordersMissing, new FixedClock(FixedNowUtc));

        var missingResult = await handlerMissing.Handle(new ExportCustomerData(missingId, OwnerActor), CancellationToken.None);
        Assert.False(missingResult.IsSuccess);
        Assert.Equal("not_found", missingResult.Error.Code);
        Assert.Equal(0, storeMissing.ExportAuditCalls);

        var customerId = Guid.CreateVersion7();
        var profile = new CustomerExportProfileView(
            customerId, "KH001000", "Khách Xuất", "+84901234567", null, null, "retail", false,
            "admin", "direct", null, "active", null, false, 0, 0L, null, null, null, null, null, FixedNowUtc, FixedNowUtc);
        var storeOk = new FakePrivacyStore
        {
            ExportSnapshot = new CustomerCrmExportSnapshot(profile, [], [], null, [], [], [], [])
        };
        var ordersOk = new FakeOrdersPort();
        var handlerOk = new ExportCustomerDataHandler(storeOk, ordersOk, new FixedClock(FixedNowUtc));

        var okResult = await handlerOk.Handle(new ExportCustomerData(customerId, OwnerActor), CancellationToken.None);
        Assert.True(okResult.IsSuccess);
        Assert.Equal(1, storeOk.ExportAuditCalls);
        Assert.Equal(customerId, okResult.Value.Customer.Id);
    }

    [Fact]
    public void PrivacyRequestsStrictJsonDeserializationRejectsDuplicateKeysUnknownFieldsAndNonObjects()
    {
        // Duplicate JSON keys rejected
        var dupJson = Encoding.UTF8.GetBytes("""{"reason":"Lý do hợp lệ","reason":"Lý do 2","requestChannel":"phone"}""");
        Assert.False(CustomerPrivacyJson.TryDeserializeObject<StrictTestPayload>(dupJson, out _));

        // Unknown property rejected (UnmappedMemberHandling = Disallow)
        var unknownJson = Encoding.UTF8.GetBytes("""{"reason":"Lý do hợp lệ","requestChannel":"phone","extra":"nope"}""");
        Assert.False(CustomerPrivacyJson.TryDeserializeObject<StrictTestPayload>(unknownJson, out _));

        // Wrong casing rejected (PropertyNameCaseInsensitive = false)
        var wrongCaseJson = Encoding.UTF8.GetBytes("""{"Reason":"Lý do hợp lệ","requestChannel":"phone"}""");
        Assert.False(CustomerPrivacyJson.TryDeserializeObject<StrictTestPayload>(wrongCaseJson, out _));

        // Non-object JSON rejected
        var arrayJson = Encoding.UTF8.GetBytes("""["a","b"]""");
        Assert.False(CustomerPrivacyJson.TryDeserializeObject<StrictTestPayload>(arrayJson, out _));

        // Valid JSON accepted
        var validJson = Encoding.UTF8.GetBytes("""{"reason":"Lý do hợp lệ","requestChannel":"phone"}""");
        Assert.True(CustomerPrivacyJson.TryDeserializeObject<StrictTestPayload>(validJson, out var parsed));
        Assert.Equal("Lý do hợp lệ", parsed!.Reason);
        Assert.Equal("phone", parsed.RequestChannel);
    }

    private sealed record StrictTestPayload(string? Reason = null, string? RequestChannel = null);

    private sealed class FixedClock(DateTime utc) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class FakePrivacyStore : ICustomerPrivacyStore
    {
        public CustomerMergePairLock PairLockResult { get; init; } = new(null, null, false, false);
        public CustomerEraseLock EraseLockResult { get; init; } = new(null, [], []);
        public CustomerCrmExportSnapshot? ExportSnapshot { get; init; }
        public int CompleteMergeCalls { get; private set; }
        public int CompleteEraseCalls { get; private set; }
        public int ExportAuditCalls { get; private set; }

        public Task AcquirePrivacyLockAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CustomerRecord?> LockCustomerForConsentAsync(Guid customerId, CancellationToken ct) => Task.FromResult<CustomerRecord?>(null);
        public Task<Result<CustomerConsentsResult>> UpsertConsentAsync(Guid customerId, string channel, string purpose, string status, string evidence, uint? expectedVersion, Guid actorId, DateTime nowUtc, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<CustomerMergePairLock> LockPairForMergeAsync(Guid targetCustomerId, Guid sourceCustomerId, CancellationToken ct)
            => Task.FromResult(PairLockResult);
        public Task<Result<CustomerDetailView>> CompleteMergeAsync(CompleteMergeInput input, CancellationToken ct)
        {
            CompleteMergeCalls++;
            throw new NotSupportedException();
        }
        public Task<CustomerEraseLock> LockForEraseAsync(Guid customerId, CancellationToken ct)
            => Task.FromResult(EraseLockResult);
        public Task<Result<CustomerEraseResult>> CompleteEraseAsync(CompleteEraseInput input, CancellationToken ct)
        {
            CompleteEraseCalls++;
            throw new NotSupportedException();
        }
        public Task<CustomerCrmExportSnapshot?> LoadForExportAsync(Guid customerId, CancellationToken ct)
            => Task.FromResult(ExportSnapshot);
        public Task RecordExportAccessAuditAsync(Guid customerId, CancellationToken ct)
        {
            ExportAuditCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrdersPort : IOrderCustomerPrivacyPort
    {
        public IReadOnlyList<string> EraseBlockers { get; init; } = [];
        public int ReassignCalls { get; private set; }
        public int DeleteCartsCalls { get; private set; }
        public int AnonymizeOrdersCalls { get; private set; }

        public Task<bool> HasOrdersAsync(Guid customerId, CancellationToken ct) => Task.FromResult(false);
        public Task<int> ReassignCustomerOrdersAsync(Guid sourceCustomerId, Guid targetCustomerId, DateTime nowUtc, CancellationToken ct)
        {
            ReassignCalls++;
            return Task.FromResult(0);
        }
        public Task<CustomerDeliveredOrderStats> ComputeDeliveredOrderStatsAsync(Guid customerId, CancellationToken ct)
            => Task.FromResult(new CustomerDeliveredOrderStats(0, 0L, null, null));
        public Task<IReadOnlyList<string>> CheckEraseBlockersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
            => Task.FromResult(EraseBlockers);
        public Task DeleteAccountCartsAsync(IReadOnlyCollection<Guid> customerAccountIds, CancellationToken ct)
        {
            DeleteCartsCalls++;
            return Task.CompletedTask;
        }
        public Task<int> AnonymizeCustomerOrdersAsync(IReadOnlyCollection<Guid> customerIds, string anonymizedText, string anonymizedPhone, DateTime nowUtc, CancellationToken ct)
        {
            AnonymizeOrdersCalls++;
            return Task.FromResult(0);
        }
        public Task<IReadOnlyList<CustomerExportOrderView>> ListOrdersForExportAsync(Guid customerId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CustomerExportOrderView>>([]);
    }

    private sealed class FakePricingPort : IPricingCustomerMergePort
    {
        public int ReassignCalls { get; private set; }
        public Task<(int MovedVouchers, int MovedRedemptions)> ReassignCustomerPricingAsync(Guid sourceCustomerId, Guid targetCustomerId, CancellationToken ct)
        {
            ReassignCalls++;
            return Task.FromResult((0, 0));
        }
    }
}
