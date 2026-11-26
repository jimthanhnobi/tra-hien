using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

public sealed record CustomerInteractionView(
    Guid Id,
    Guid CustomerId,
    Guid StaffId,
    string? StaffDisplayName,
    string Channel,
    string Kind,
    string Content,
    DateTime OccurredAt,
    Guid? OrderId,
    string? OrderCode,
    Guid? CampaignId,
    DateTime? FollowUpAt,
    DateTime? FollowUpDoneAt,
    string? FollowUpResult,
    DateTime CreatedAt);

public sealed record FollowUpItemView(
    Guid InteractionId,
    Guid CustomerId,
    string CustomerCode,
    string CustomerFullName,
    string? CustomerPhoneE164,
    string? CustomerZaloName,
    string CustomerGroup,
    bool CustomerIsVip,
    string CustomerContactStatus,
    Guid StaffId,
    string? StaffDisplayName,
    string Channel,
    string Kind,
    string Content,
    DateTime OccurredAt,
    Guid? OrderId,
    string? OrderCode,
    DateTime FollowUpAt,
    bool IsOverdue,
    DateTime CreatedAt);

public sealed class InteractionRecord
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid StaffId { get; set; }
    public string Channel { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? CampaignId { get; set; }
    public DateTime? FollowUpAt { get; set; }
    public DateTime? FollowUpDoneAt { get; set; }
    public string? FollowUpResult { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record FollowUpWindow(string DueMode, DateTime CutoffExclusiveUtc, DateTime NowUtc);

public sealed record ListCustomerInteractions(Guid CustomerId, int Page, int PageSize)
    : IQuery<CatalogPage<CustomerInteractionView>>;

public sealed record CreateCustomerInteraction(
    Guid CustomerId,
    string Channel,
    string Kind,
    string Content,
    string? OccurredAt,
    Guid? OrderId,
    string? FollowUpAt,
    CustomerActor Actor) : ICommand<CustomerInteractionView>;

public sealed record PatchCustomerInteraction(
    Guid CustomerId,
    Guid InteractionId,
    JsonElement Fields,
    CustomerActor Actor) : ICommand<CustomerInteractionView>;

public sealed record CompleteInteractionFollowUp(
    Guid CustomerId,
    Guid InteractionId,
    string Result,
    CustomerActor Actor) : ICommand<CustomerInteractionView>;

public sealed record ListFollowUps(string? Due, int Page, int PageSize)
    : IQuery<CatalogPage<FollowUpItemView>>;

public interface IInteractionQueries
{
    Task<CatalogPage<CustomerInteractionView>> ListByCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken ct);
    Task<CatalogPage<FollowUpItemView>> ListFollowUpsAsync(FollowUpWindow window, int page, int pageSize, CancellationToken ct);
}

public interface IInteractionStore
{
    Task<CustomerRecord?> LockCustomerAsync(Guid customerId, CancellationToken ct);
    Task<InteractionRecord?> LockInteractionAsync(Guid customerId, Guid interactionId, CancellationToken ct);
    Task<Result<CustomerInteractionView>> CreateAsync(InteractionRecord interaction, DateTime nowUtc, CancellationToken ct);
    Task<Result<CustomerInteractionView>> UpdateAsync(InteractionRecord interaction, bool recomputeLastContacted, DateTime nowUtc, CancellationToken ct);
    Task<Result<CustomerInteractionView>> CompleteFollowUpAsync(Guid customerId, Guid interactionId, DateTime doneAtUtc, string result, CancellationToken ct);
}

public static class FollowUpWindowCalculator
{
    // ASSUMPTION-P204-01 = A (OWNER_APPROVED 2026-11-26):
    // - overdue: follow_up_at < clock.UtcNow
    // - today (default): follow_up_at < ToUtc(vnToday.AddDays(1), 00:00)
    // - week: follow_up_at < ToUtc(vnToday.AddDays(7), 00:00)
    public static FollowUpWindow Compute(string? due, DateTimeOffset nowOffset)
    {
        var mode = due ?? InteractionRules.DueToday;
        var nowUtc = nowOffset.UtcDateTime;
        var vnToday = BusinessCalendar.ToVietnamDate(nowOffset);

        var cutoffExclusiveUtc = mode switch
        {
            InteractionRules.DueOverdue => nowUtc,
            InteractionRules.DueToday => BusinessCalendar.ToUtc(vnToday.AddDays(1), TimeOnly.MinValue).UtcDateTime,
            InteractionRules.DueWeek => BusinessCalendar.ToUtc(vnToday.AddDays(7), TimeOnly.MinValue).UtcDateTime,
            _ => throw new ArgumentOutOfRangeException(nameof(due), "Chế độ lọc lịch hẹn không hợp lệ.")
        };

        return new FollowUpWindow(mode, cutoffExclusiveUtc, nowUtc);
    }
}

public static partial class InteractionTimestampParser
{
    [GeneratedRegex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:[Zz]|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictIso8601UtcOffset();

    // Strict ISO 8601 date-time with 'T' separator, optional 1..7 fractional digits, and explicit UTC offset ('Z'/'z' or ±HH:MM).
    public static bool TryParseUtc(string? raw, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(raw) || raw.Length > 40 || !CatalogInput.WithoutNul(raw))
        {
            return false;
        }

        if (!StrictIso8601UtcOffset().IsMatch(raw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return false;
        }

        utc = dto.UtcDateTime;
        return true;
    }

    // PostgreSQL timestamptz stores microsecond precision (1 us = 10 ticks).
    // Truncate sub-microsecond ticks after strict tick-level future checks so persisted invariants match DB values identically.
    public static DateTime TruncateToMicroseconds(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
}

public static class InteractionJson
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
}
