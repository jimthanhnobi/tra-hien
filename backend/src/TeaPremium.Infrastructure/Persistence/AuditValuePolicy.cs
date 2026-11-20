using System.Text.Json;

namespace TeaPremium.Infrastructure.Persistence;

// Audit is an allowlist. New fields and JSON paths remain useful as evidence of a change,
// but their values are never copied into the audit row until explicitly classified.
public static class AuditValuePolicy
{
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> SafeNumericSettings = new(StringComparer.Ordinal)
    {
        "shipping.flat_fee_vnd", "shipping.free_threshold_vnd",
        "orders.bank_transfer_hold_hours", "orders.max_quantity_per_line_web",
        "orders.max_staff_discount_percent", "returns.window_days",
        "marketing.min_days_between_messages", "referral.max_rewards_per_year",
        "referral.welcome_discount_vnd", "referral.welcome_min_order_vnd",
        "referral.qualify_after_days", "seo.sold_unique_keep_days",
        "retention.carts_days", "retention.link_clicks_days", "retention.idempotency_hours",
        "retention.notification_logs_days", "retention.abandoned_upload_hours",
        "retention.web_vitals_days"
    };

    private static readonly HashSet<string> SafeBooleanSettings = new(StringComparer.Ordinal)
    {
        "orders.cod_requires_call", "orders.auto_confirm_on_full_payment",
        "seo.organization.has_store"
    };

    private static readonly HashSet<string> SafeNumericProperties = new(StringComparer.Ordinal)
    {
        "amount_vnd", "price_vnd", "cost_vnd", "sort_order", "quantity", "updated_at"
    };

    private static readonly HashSet<string> SafeBooleanProperties = new(StringComparer.Ordinal)
    {
        "is_active"
    };

    private static readonly HashSet<string> SafeStatusValues = new(StringComparer.Ordinal)
    {
        "active", "inactive", "draft", "pending", "confirmed", "cancelled", "archived"
    };

    private static readonly HashSet<string> KnownJsonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bank_name", "account_number", "account_name", "phone", "email",
        "full_name", "receiver_name", "receiver_phone", "receiver_email", "notes",
        "address", "address_line", "contacts", "items", "status", "amount_vnd",
        "min", "max", "type", "value", "max_discount_vnd", "min_order_vnd",
        "valid_days", "logo_asset_id", "same_as", "has_store", "geo",
        "opening_hours", "category", "days", "ga4_measurement_id"
    };

    public static object? Filter(string entityType, string? settingKey, string propertyName, object? value)
    {
        var field = ToSnakeCase(propertyName);
        if (value is null) return null;
        if (entityType == "platform.settings" && field == "value")
        {
            try
            {
                using var document = JsonDocument.Parse((string)value);
                return FilterJson(document.RootElement, settingKey ?? "", "");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Audited JSON value is invalid.");
            }
        }

        if (IsSensitive(field)) return Redacted;
        // P1-04 entity/path classification. Never whitelist names/biographies/HTML globally.
        if (entityType is "catalog.categories" or "catalog.collections" or "catalog.materials" or "catalog.artisans")
        {
            if (field == "status" && value is "visible" or "hidden" or "draft" or "published" or "archived") return value;
            if (field == "robots_noindex" && value is bool) return value;
            if (field == "first_published_at" && value is DateTime) return value;
        }
        if (field == "status" && value is string status && SafeStatusValues.Contains(status)) return status;
        if (SafeNumericProperties.Contains(field) && IsNumber(value)) return value;
        if (SafeBooleanProperties.Contains(field) && value is bool) return value;
        if (field == "updated_at" && value is DateTime) return value;
        return Redacted;
    }

    private static object? FilterJson(JsonElement element, string key, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in element.EnumerateObject())
            {
                // JSON property names are untrusted too. Never echo unknown names: they can be PII.
                if (!KnownJsonFields.Contains(item.Name))
                {
                    result["unclassified"] = Redacted;
                    continue;
                }
                var name = item.Name.ToLowerInvariant();
                var next = path.Length == 0 ? name : $"{path}.{name}";
                result[name] = FilterJson(item.Value, key, next);
            }
            return result;
        }

        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(item => FilterJson(item, key, path)).ToArray();

        var fullPath = path.Length == 0 ? key : $"{key}.{path}";
        if (IsSensitive(fullPath)) return Redacted;
        if (element.ValueKind == JsonValueKind.Null
            && (IsAllowedNumber(key, path) || SafeBooleanSettings.Contains(fullPath))) return null;
        if (element.ValueKind == JsonValueKind.Number && IsAllowedNumber(key, path)) return element.Clone();
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False && SafeBooleanSettings.Contains(fullPath))
            return element.GetBoolean();
        if (element.ValueKind == JsonValueKind.String && key == "payment.methods_enabled"
            && path.Length == 0 && element.GetString() is "cod" or "bank_transfer")
            return element.GetString();
        if (element.ValueKind == JsonValueKind.String && fullPath == "referral.reward_voucher.type"
            && element.GetString() is "fixed" or "percent")
            return element.GetString();
        return Redacted;
    }

    private static bool IsAllowedNumber(string key, string path)
    {
        if (path.Length == 0) return SafeNumericSettings.Contains(key);
        return (key is "seo.handling_days" or "seo.transit_days" && path is "min" or "max")
            || (key == "seo.return_policy" && path == "days")
            || (key == "referral.reward_voucher" && path is "value" or "max_discount_vnd" or "min_order_vnd" or "valid_days");
    }

    private static bool IsSensitive(string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Contains("phone", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("email", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("address", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("name", StringComparison.OrdinalIgnoreCase) && segment is not "bank_name"
            || segment.Contains("receiver", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("note", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("account", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("token", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("password", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("chat_id", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("hotline", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string ToSnakeCase(string name)
    {
        var chars = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && chars.Length > 0) chars.Append('_');
            chars.Append(char.ToLowerInvariant(c));
        }
        return chars.ToString();
    }
}
