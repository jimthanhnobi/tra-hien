using System.Globalization;
using System.Text.Json;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

public sealed record CustomerActor(Guid Id, string[] Permissions, bool IsOwner)
{
    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

public sealed record CustomerTagView(Guid Id, string Name, string? Color);

public sealed record CustomerAddressView(
    Guid Id,
    Guid CustomerId,
    string? Label,
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string ProvinceName,
    string? WardCode,
    string? WardName,
    string? WardNameText,
    string AddressLine,
    bool IsDefault,
    bool IsLocationActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CustomerInterestView(
    Guid Id,
    string InterestType,
    Guid? RefId,
    string? PriceBand,
    string Source,
    int Score,
    DateTime UpdatedAt);

public sealed record CustomerConsentView(
    Guid Id,
    string Channel,
    string Purpose,
    string Status,
    string Source,
    DateTime CapturedAt);

public sealed record CustomerAccountSummaryView(
    bool HasAccount,
    string? Status,
    DateTime? PhoneVerifiedAt,
    DateTime? EmailVerifiedAt,
    DateTime? HistoryUnlockedAt,
    DateTime? LastLoginAt);

public sealed record CustomerStatsView(
    bool HadLegacyPurchase,
    int DeliveredOrdersCount,
    long LifetimeValueVnd,
    DateTime? FirstDeliveredAt,
    DateTime? LastDeliveredAt,
    DateTime? LastContactedAt);

public sealed record CustomerSummaryView(
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
    string ContactStatus,
    bool HadLegacyPurchase,
    int DeliveredOrdersCount,
    long LifetimeValueVnd,
    DateTime? LastDeliveredAt,
    DateTime? LastContactedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion,
    IReadOnlyList<CustomerTagView> Tags);

public sealed record CustomerDetailView(
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
    Guid? MergedIntoCustomerId,
    DateTime? ErasedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion,
    CustomerStatsView Stats,
    IReadOnlyList<CustomerTagView> Tags,
    IReadOnlyList<CustomerAddressView> Addresses,
    IReadOnlyList<CustomerInterestView> Interests,
    IReadOnlyList<CustomerConsentView> Consents,
    CustomerAccountSummaryView AccountSummary);

public sealed record CustomerTagsResult(Guid CustomerId, string RowVersion, IReadOnlyList<CustomerTagView> Tags);

public sealed class CustomerRecord
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? PhoneE164 { get; set; }
    public string? Email { get; set; }
    public string? ZaloName { get; set; }
    public string CustomerGroup { get; set; } = "retail";
    public bool IsVip { get; set; }
    public string Origin { get; set; } = "admin";
    public string AcquisitionSource { get; set; } = "unknown";
    public Guid? ReferredByCustomerId { get; set; }
    public string ContactStatus { get; set; } = "active";
    public string? NoteSummary { get; set; }
    public bool HadLegacyPurchase { get; set; }
    public DateTime? FirstDeliveredAt { get; set; }
    public DateTime? LastDeliveredAt { get; set; }
    public int DeliveredOrdersCount { get; set; }
    public long LifetimeValueVnd { get; set; }
    public DateTime? LastContactedAt { get; set; }
    public string SearchText { get; set; } = "";
    public Guid? MergedIntoCustomerId { get; set; }
    public DateTime? ErasedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class CustomerAddressRecord
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? Label { get; set; }
    public string ReceiverName { get; set; } = "";
    public string ReceiverPhone { get; set; } = "";
    public string ProvinceCode { get; set; } = "";
    public string? WardCode { get; set; }
    public string? WardNameText { get; set; }
    public string AddressLine { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record CustomerIdentityCorrectionAudit(
    Guid CustomerId,
    string[] ChangedFields,
    string? BeforeOrigin,
    string? AfterOrigin,
    string? BeforeAcquisitionSource,
    string? AfterAcquisitionSource,
    bool? BeforeHadLegacyPurchase,
    bool? AfterHadLegacyPurchase,
    bool PhoneChanged,
    string Reason);

public sealed record CustomerListFilter(
    int Page,
    int PageSize,
    string Sort,
    string? Q,
    string? PhoneSearchDigits,
    string? Group,
    Guid? TagId,
    string? InterestType,
    Guid? InterestRefId,
    string? InterestPriceBand,
    DateTime? LastDeliveredBeforeUtc,
    DateTime? LastDeliveredAfterUtc,
    long? MinLtv,
    string? ContactStatus,
    string? ConsentChannel,
    string? ConsentPurpose);

// Queries & Commands
public sealed record ListCustomers(
    int Page,
    int PageSize,
    string? Sort,
    string? Q,
    string? Group,
    Guid? Tag,
    string? Interest,
    string? LastDeliveredBefore,
    string? LastDeliveredAfter,
    long? MinLtv,
    string? ContactStatus,
    string? HasConsent) : IQuery<CatalogPage<CustomerSummaryView>>;

public sealed record GetCustomer(Guid Id) : IQuery<CustomerDetailView>;

public sealed record ListCustomerOrders(Guid CustomerId, int Page, int PageSize) : IQuery<CatalogPage<CustomerOrderSummaryView>>;

public sealed record CreateCustomer(
    string FullName,
    string? Phone,
    string? Email,
    string? ZaloName,
    string? CustomerGroup,
    bool IsVip,
    string? AcquisitionSource,
    Guid? ReferredByCustomerId,
    string? ContactStatus,
    string? NoteSummary,
    bool HadLegacyPurchase,
    Guid[]? TagIds,
    CustomerActor Actor) : ICommand<CustomerDetailView>;

public sealed record PatchCustomer(Guid Id, JsonElement Fields, CustomerActor Actor) : ICommand<CustomerDetailView>;

public sealed record CorrectCustomerIdentity(Guid Id, JsonElement Fields, CustomerActor Actor) : ICommand<CustomerDetailView>;

public sealed record ListTags : IQuery<IReadOnlyList<CustomerTagView>>;

public sealed record CreateTag(string Name, string? Color) : ICommand<CustomerTagView>;

public sealed record PatchTag(Guid Id, JsonElement Fields) : ICommand<CustomerTagView>;

public sealed record PutCustomerTags(Guid CustomerId, Guid[] TagIds, string? RowVersion) : ICommand<CustomerTagsResult>;

public sealed record ListCustomerAddresses(Guid CustomerId) : IQuery<IReadOnlyList<CustomerAddressView>>;

public sealed record GetCustomerAddress(Guid CustomerId, Guid AddressId) : IQuery<CustomerAddressView>;

public sealed record CreateCustomerAddress(
    Guid CustomerId,
    string? Label,
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string? WardCode,
    string? WardNameText,
    string AddressLine,
    bool IsDefault,
    string? RowVersion) : ICommand<CustomerAddressView>;

public sealed record PatchCustomerAddress(Guid CustomerId, Guid AddressId, JsonElement Fields) : ICommand<CustomerAddressView>;

public sealed record DeleteCustomerAddress(Guid CustomerId, Guid AddressId, string? RowVersion) : ICommand<bool>;

// Ports
public interface ICustomerQueries
{
    Task<CatalogPage<CustomerSummaryView>> ListAsync(CustomerListFilter filter, CancellationToken ct);
    Task<CustomerDetailView?> GetDetailAsync(Guid id, CancellationToken ct);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CustomerTagView>> ListTagsAsync(CancellationToken ct);
    Task<IReadOnlyList<CustomerAddressView>> ListAddressesAsync(Guid customerId, CancellationToken ct);
    Task<CustomerAddressView?> GetAddressAsync(Guid customerId, Guid addressId, CancellationToken ct);
}

public interface ICustomerStore
{
    Task<string> AllocateCustomerCodeAsync(CancellationToken ct);
    Task<string> ComputeSearchTextAsync(string source, CancellationToken ct);
    Task<Guid?> FindActiveCustomerIdByPhoneAsync(string phoneE164, Guid? excludeCustomerId, CancellationToken ct);
    Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct);
    Task<bool> HasCustomerAccountAsync(Guid customerId, CancellationToken ct);
    Task<CustomerRecord?> LockCustomerAsync(Guid id, CancellationToken ct);
    Task<bool> AllTagsExistAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken ct);
    Task<Result<CustomerDetailView>> CreateCustomerAsync(CustomerRecord customer, IReadOnlyList<Guid> tagIds, CancellationToken ct);
    Task<Result<CustomerDetailView>> UpdateCustomerAsync(CustomerRecord customer, uint expectedVersion, CustomerIdentityCorrectionAudit? identityAudit, CancellationToken ct);
    Task<Result<CustomerTagView>> CreateTagAsync(Guid id, string name, string? color, CancellationToken ct);
    Task<Result<CustomerTagView>> UpdateTagAsync(Guid id, string? name, bool hasName, string? color, bool hasColor, CancellationToken ct);
    Task<Result<CustomerTagsResult>> ReplaceCustomerTagsAsync(Guid customerId, uint? expectedVersion, IReadOnlyList<Guid> tagIds, DateTime nowUtc, CancellationToken ct);
    Task<CustomerAddressRecord?> LoadAddressAsync(Guid customerId, Guid addressId, CancellationToken ct);
    Task<Result<CustomerAddressView>> CreateAddressAsync(CustomerAddressRecord address, uint? expectedCustomerVersion, DateTime nowUtc, CancellationToken ct);
    Task<Result<CustomerAddressView>> UpdateAddressAsync(CustomerAddressRecord address, uint? expectedCustomerVersion, DateTime nowUtc, CancellationToken ct);
    Task<Result<bool>> DeleteAddressAsync(Guid customerId, Guid addressId, uint? expectedCustomerVersion, DateTime nowUtc, CancellationToken ct);
}

public static class CustomerFilterParser
{
    public static bool TryParseInterest(string? raw, out string? type, out Guid? refId, out string? priceBand)
    {
        type = null;
        refId = null;
        priceBand = null;
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!CatalogInput.WithoutNul(raw))
        {
            return false;
        }

        var colon = raw.IndexOf(':');
        if (colon <= 0 || colon != raw.LastIndexOf(':') || colon == raw.Length - 1)
        {
            return false;
        }

        var kind = raw[..colon];
        var value = raw[(colon + 1)..];
        if (!CustomerRules.InterestTypes.Contains(kind))
        {
            return false;
        }

        if (kind == "price_band")
        {
            if (!CustomerRules.PriceBands.Contains(value))
            {
                return false;
            }

            type = kind;
            priceBand = value;
            return true;
        }

        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        type = kind;
        refId = parsed;
        return true;
    }

    public static bool TryParseConsent(string? raw, out string? channel, out string? purpose)
    {
        channel = null;
        purpose = null;
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!CatalogInput.WithoutNul(raw))
        {
            return false;
        }

        var colon = raw.IndexOf(':');
        if (colon <= 0 || colon != raw.LastIndexOf(':') || colon == raw.Length - 1)
        {
            return false;
        }

        var ch = raw[..colon];
        var pu = raw[(colon + 1)..];
        if (!CustomerRules.ConsentChannels.Contains(ch) || !CustomerRules.ConsentPurposes.Contains(pu))
        {
            return false;
        }

        channel = ch;
        purpose = pu;
        return true;
    }

    public static bool TryParseDateFilter(string? raw, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!CatalogInput.WithoutNul(raw) || raw.Length > 40)
        {
            return false;
        }

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            utc = BusinessCalendar.ToUtc(date, TimeOnly.MinValue).UtcDateTime;
            return true;
        }

        if (!HasExplicitUtcOffset(raw))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        return false;
    }

    private static bool HasExplicitUtcOffset(string value)
        => value.EndsWith('Z') || (value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':');
}
