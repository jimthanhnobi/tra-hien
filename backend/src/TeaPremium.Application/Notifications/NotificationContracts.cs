using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeaPremium.Application.Notifications;

public static class NotificationChannels
{
    public const string Telegram = "telegram";
    public const string Email = "email";
}

public static class NotificationTemplateNames
{
    // P2-13 shop templates (BR-PLT-03 + GAP-03 3A).
    public const string OrderPlacedShop = "order_placed_shop";
    public const string DailyDigestShop = "daily_digest_shop";
    public const string InventoryReconciliationShop = "inventory_reconciliation_shop";
    public const string OutboxDeadShop = "outbox_dead_shop";

    // Reserved customer template keys from BR-PLT-03 for P3-06 / P5-05.
    public const string OrderPlacedCustomer = "order_placed_customer";
    public const string PaymentReceivedCustomer = "payment_received_customer";
    public const string OrderShippedCustomer = "order_shipped_customer";
    public const string OrderCancelledCustomer = "order_cancelled_customer";
    public const string ReferralRewardCustomer = "referral_reward_customer";
}

public static class NotificationRefTypes
{
    public const string Order = "order";
    public const string DailyDigest = "daily_digest";
    public const string InventoryReconciliation = "inventory_reconciliation";
    public const string OutboxDead = "outbox_dead";
}

public static class NotificationContracts
{
    public const string OrderPlacedMessageType = "OrderPlaced";
    public const string DeliveryMessageType = "NotificationDelivery";
    public const int DailyDigestLowStockMaxItems = 10;
    public const int TelegramMaxMessageLength = 4096;

    // GAP-02 = 2A (ASSUMPTION owner-approved 2026-11-23): shared 24-hour expiring payment window.
    public static readonly TimeSpan OrderPaymentExpiringWindow = TimeSpan.FromHours(24);
    public static readonly TimeOnly DailyDigestScheduledVietnamTime = new(8, 0);

    public static string NormalizeTelegramChatId(string chatId) => chatId.Trim();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string HashRecipient(string normalizedRecipient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRecipient);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRecipient)));
    }

    public static bool ShouldDeliverForWatermark(DateTime occurredAtUtc, DateTime? settingUpdatedAtUtc) =>
        settingUpdatedAtUtc is { } updatedAt && occurredAtUtc >= updatedAt;

    public static IReadOnlyList<string> DeduplicateShopEmails(IEnumerable<string?> rawEmails)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in rawEmails)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl)) continue;
            var trimmed = raw.Trim();
            if (!EmailSecurity.IsValidEmailAddress(trimmed)) continue;
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    public static Guid DailyDigestRefId(DateOnly vietnamDate) =>
        DeterministicGuid($"TeaPremium.Notification:{NotificationTemplateNames.DailyDigestShop}:{vietnamDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

    public static Guid InventoryReconciliationRefId(Guid variantId, DateOnly vietnamDate) =>
        DeterministicGuid($"TeaPremium.Notification:{NotificationTemplateNames.InventoryReconciliationShop}:{variantId:D}:{vietnamDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

    private static Guid DeterministicGuid(string input)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        // Set RFC 4122 version 5 / variant bits for stable UUID formatting.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

// BR-PLT-03 order_placed_shop: statically excludes customer PII (phone, address, name, email, notes) and costs.
public sealed record OrderPlacedNotification(
    Guid OrderId,
    string OrderCode,
    long TotalVnd,
    string PaymentMethod,
    int ItemCount,
    bool IsBackfilled = false)
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string Serialize(OrderPlacedNotification value) =>
        JsonSerializer.Serialize(value, WriteOptions);

    public static bool TryParse(string? payload, out OrderPlacedNotification value)
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var parsed = document.RootElement.Deserialize<OrderPlacedNotification>(ReadOptions);
            if (parsed is null
                || parsed.OrderId == Guid.Empty
                || string.IsNullOrWhiteSpace(parsed.OrderCode)
                || parsed.OrderCode.Length > 40
                || parsed.OrderCode.Any(char.IsControl)
                || parsed.TotalVnd < 0
                || parsed.PaymentMethod is not ("cod" or "bank_transfer")
                || parsed.ItemCount <= 0)
                return false;
            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// Single-channel, single-recipient delivery outbox payload. Carries RecipientHash only (no plaintext email/chat_id).
public sealed record NotificationDeliveryPayload(
    string Channel,
    string Template,
    string RefType,
    Guid RefId,
    string RecipientHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject,
    string TextBody,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HtmlBody)
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string Serialize(NotificationDeliveryPayload value) =>
        JsonSerializer.Serialize(value, WriteOptions);

    public static bool TryParse(string? payload, out NotificationDeliveryPayload value)
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var parsed = document.RootElement.Deserialize<NotificationDeliveryPayload>(ReadOptions);
            if (parsed is null
                || parsed.Channel is not (NotificationChannels.Telegram or NotificationChannels.Email)
                || string.IsNullOrWhiteSpace(parsed.Template)
                || parsed.Template.Length > 64
                || parsed.Template.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(parsed.RefType)
                || parsed.RefType.Length > 64
                || parsed.RefType.Any(char.IsControl)
                || parsed.RefId == Guid.Empty
                || !IsSha256Hex(parsed.RecipientHash)
                || string.IsNullOrWhiteSpace(parsed.TextBody))
                return false;
            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

// BR-PLT-03 daily_digest_shop: statically excludes PII, artisan/product names, and cost fields.
public sealed record DailyDigestLowStockItem(string Sku, int Available, int LowStockThreshold);

public sealed record DailyDigestSnapshot(
    DateOnly VietnamDate,
    long PendingConfirmationOrders,
    long ExpiringPaymentOrders,
    long UnsettledCodCount,
    long UnsettledCodTotalVnd,
    long FollowUpsDueToday,
    long LowStockTotal,
    IReadOnlyList<DailyDigestLowStockItem> LowStockItems,
    long ReconciliationMismatchTotal,
    long DeadOutboxCount);

public interface IDailyDigestRead
{
    Task<DailyDigestSnapshot> ReadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

// GAP-03 3A immediate technical shop alerts: statically free of PII, artisan/product names, and costs.
public sealed record InventoryReconciliationAlertModel(
    Guid VariantId,
    string Sku,
    int OnHand,
    long LedgerQuantity,
    DateOnly VietnamDate,
    int Reserved = 0,
    long ActiveReservationQuantity = 0);

public sealed record OutboxDeadAlertModel(
    Guid MessageId,
    string MessageType,
    int Attempts,
    string ErrorCode);
