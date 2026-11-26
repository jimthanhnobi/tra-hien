using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

// NFR-04 / G22: Order* outbox payload never contains PII, free-text notes, addresses, or cost fields.
public sealed record OrderOutboxPayload(
    Guid OrderId,
    string OrderCode,
    string Status,
    DateTimeOffset OccurredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasonCode = null);

public static class OrderOutboxMessage
{
    private static readonly HashSet<string> AllowedReasonCodes = new(StringComparer.Ordinal)
    {
        "payment_expired",
        OrderConfirmReasonCodes.AutoConfirmedOnFullPayment
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string Serialize(OrderOutboxPayload payload) =>
        JsonSerializer.Serialize(payload, WriteOptions);

    public static bool TryParse(string? payload, out OrderOutboxPayload parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var value = document.RootElement.Deserialize<OrderOutboxPayload>(ReadOptions);
            if (value is null
                || value.OrderId == Guid.Empty
                || !OrderCode.IsValid(value.OrderCode)
                || !OrderStatus.IsValid(value.Status)
                || value.OccurredAt == default
                || (value.ReasonCode is not null && !AllowedReasonCodes.Contains(value.ReasonCode)))
                return false;

            parsed = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// G22: future phases (P2-11 CRM metrics/interests, P2-13/P3-06 notifications, P5-05 referrals) register
// IOrderOutboxStep implementations rather than adding a second IOutboxHandler for any Order* type.
public interface IOrderOutboxStep
{
    string MessageType { get; }
    int Order { get; }
    Task HandleAsync(Guid messageId, OrderOutboxPayload payload, CancellationToken ct);
}
