using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Crm;

public sealed record CustomerConsentItemView(
    Guid Id,
    string Channel,
    string Purpose,
    string Status,
    string Source,
    string Evidence,
    DateTime CapturedAt,
    Guid? CapturedBy);

public sealed record CustomerConsentsResult(
    Guid CustomerId,
    string RowVersion,
    CustomerConsentItemView Upserted,
    IReadOnlyList<CustomerConsentItemView> Consents);

public sealed record CustomerEraseResult(
    Guid CustomerId,
    string Code,
    string ContactStatus,
    DateTime ErasedAt,
    string RowVersion,
    Guid DataRequestId,
    int AnonymizedOrdersCount,
    int ScrubbedMergedStubsCount);

// BR-CRM-13 (P2-12): full personal data export DTOs; strictly exclude secrets, costs and internal notes.
public sealed record CustomerExportProfileView(
    Guid Id,
    string Code,
    string FullName,
    string? PhoneE164,
    string? Email,
    string? ZaloName,
    string CustomerGroup,
    bool IsVip,
    string Origin,
    string AcquisitionSource,
    Guid? ReferredByCustomerId,
    string ContactStatus,
    string? NoteSummary,
    bool HadLegacyPurchase,
    int DeliveredOrdersCount,
    long LifetimeValueVnd,
    DateTime? FirstDeliveredAt,
    DateTime? LastDeliveredAt,
    DateTime? LastContactedAt,
    Guid? MergedIntoCustomerId,
    DateTime? ErasedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CustomerExportLoginView(
    string Provider,
    string? Email,
    DateTime CreatedAt);

public sealed record CustomerExportAccountView(
    Guid Id,
    string? LoginPhone,
    string? Email,
    DateTime? EmailVerifiedAt,
    DateTime? PhoneVerifiedAt,
    DateTime? HistoryUnlockedAt,
    string Status,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    IReadOnlyList<CustomerExportLoginView> ExternalLogins);

public sealed record CustomerExportInteractionView(
    Guid Id,
    string Channel,
    string Kind,
    string Content,
    DateTime OccurredAt,
    Guid? OrderId,
    DateTime? FollowUpAt,
    DateTime? FollowUpDoneAt,
    string? FollowUpResult,
    DateTime CreatedAt);

public sealed record CustomerExportDataRequestView(
    Guid Id,
    string RequestType,
    string Status,
    string Channel,
    string? Note,
    DateTime RequestedAt,
    DateTime? CompletedAt);

public sealed record CustomerDataExportView(
    DateTime ExportedAt,
    CustomerExportProfileView Customer,
    IReadOnlyList<CustomerTagView> Tags,
    IReadOnlyList<CustomerAddressView> Addresses,
    CustomerExportAccountView? Account,
    IReadOnlyList<CustomerConsentItemView> Consents,
    IReadOnlyList<CustomerInterestView> Interests,
    IReadOnlyList<CustomerExportInteractionView> Interactions,
    IReadOnlyList<CustomerExportDataRequestView> DataRequests,
    IReadOnlyList<CustomerExportOrderView> Orders);

// Internal store models & audit payloads
public sealed record CustomerMergePairLock(
    CustomerRecord? Target,
    CustomerRecord? Source,
    bool TargetHasAccount,
    bool SourceHasAccount);

public sealed record CustomerEraseLock(
    CustomerRecord? Customer,
    IReadOnlyList<Guid> AllCustomerIds,
    IReadOnlyList<Guid> AccountIds);

public sealed record CompleteMergeInput(
    Guid TargetCustomerId,
    Guid SourceCustomerId,
    uint? ExpectedTargetVersion,
    uint? ExpectedSourceVersion,
    bool TargetHadOrders,
    int MovedOrdersCount,
    int MovedVouchersCount,
    int MovedRedemptionsCount,
    CustomerDeliveredOrderStats DeliveredStats,
    DateTime NowUtc);

public sealed record CompleteEraseInput(
    Guid CustomerId,
    string Reason,
    string RequestChannel,
    uint? ExpectedVersion,
    Guid ActorId,
    int AnonymizedOrdersCount,
    DateTime NowUtc);

public sealed record CustomerCrmExportSnapshot(
    CustomerExportProfileView Customer,
    IReadOnlyList<CustomerTagView> Tags,
    IReadOnlyList<CustomerAddressView> Addresses,
    CustomerExportAccountView? Account,
    IReadOnlyList<CustomerConsentItemView> Consents,
    IReadOnlyList<CustomerInterestView> Interests,
    IReadOnlyList<CustomerExportInteractionView> Interactions,
    IReadOnlyList<CustomerExportDataRequestView> DataRequests);

public sealed record CustomerMergeAudit(
    Guid TargetCustomerId,
    Guid MergedCustomerId,
    int FlattenedChainCount,
    bool AccountTransferred,
    bool HistoryUnlockReset,
    int MovedOrdersCount,
    int MovedInteractionsCount,
    int MovedVouchersCount,
    int MovedRedemptionsCount);

public sealed record CustomerEraseAudit(
    Guid CustomerId,
    Guid DataRequestId,
    string RequestChannel,
    int AnonymizedOrdersCount,
    int ScrubbedInteractionsCount,
    int ScrubbedMergedStubsCount,
    bool AccountDisabled,
    string Reason);

// Commands (Export implements ICommand so read + audit action='access' share one atomic transaction)
public sealed record PutCustomerConsent(
    Guid CustomerId,
    string Channel,
    string Purpose,
    string Status,
    string Evidence,
    string? RowVersion,
    CustomerActor Actor) : ICommand<CustomerConsentsResult>;

public sealed record MergeCustomers(
    Guid TargetCustomerId,
    Guid MergeCustomerId,
    string? RowVersion,
    string? MergeRowVersion,
    CustomerActor Actor) : ICommand<CustomerDetailView>;

public sealed record EraseCustomer(
    Guid CustomerId,
    string Reason,
    string RequestChannel,
    string? RowVersion,
    CustomerActor Actor) : ICommand<CustomerEraseResult>;

public sealed record ExportCustomerData(
    Guid CustomerId,
    CustomerActor Actor) : ICommand<CustomerDataExportView>;

// Store port (implemented inside TeaPremium.Infrastructure.Crm touching only crm.* and platform.audit_logs)
public interface ICustomerPrivacyStore
{
    Task AcquirePrivacyLockAsync(CancellationToken ct);
    Task<CustomerRecord?> LockCustomerForConsentAsync(Guid customerId, CancellationToken ct);
    Task<Result<CustomerConsentsResult>> UpsertConsentAsync(
        Guid customerId,
        string channel,
        string purpose,
        string status,
        string evidence,
        uint? expectedVersion,
        Guid actorId,
        DateTime nowUtc,
        CancellationToken ct);
    Task<CustomerMergePairLock> LockPairForMergeAsync(Guid targetCustomerId, Guid sourceCustomerId, CancellationToken ct);
    Task<Result<CustomerDetailView>> CompleteMergeAsync(CompleteMergeInput input, CancellationToken ct);
    Task<CustomerEraseLock> LockForEraseAsync(Guid customerId, CancellationToken ct);
    Task<Result<CustomerEraseResult>> CompleteEraseAsync(CompleteEraseInput input, CancellationToken ct);
    Task<CustomerCrmExportSnapshot?> LoadForExportAsync(Guid customerId, CancellationToken ct);
    Task RecordExportAccessAuditAsync(Guid customerId, CancellationToken ct);
}

public static class CustomerPrivacyJson
{
    public static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowDuplicateProperties = false
    };

    public static readonly JsonSerializerOptions StrictSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static bool TryDeserializeObject<T>(ReadOnlySpan<byte> utf8Bytes, out T? value) where T : class
    {
        value = null;
        if (utf8Bytes.IsEmpty)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8Bytes.ToArray(), StrictDocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            value = JsonSerializer.Deserialize<T>(utf8Bytes, StrictSerializerOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
