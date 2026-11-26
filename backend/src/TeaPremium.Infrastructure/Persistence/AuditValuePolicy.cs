using System.Text.Json;
using TeaPremium.Domain.Crm;

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

        // P2-02 CRM fail-closed classification. All PII, notes, search_text, tag names and account secrets stay redacted.
        if (entityType == "crm.customers")
        {
            if (field == "code" && value is string customerCode && CustomerRules.IsValidCustomerCode(customerCode)) return customerCode;
            if (field == "customer_group" && value is string group && CustomerRules.CustomerGroups.Contains(group)) return group;
            if (field is "is_vip" or "had_legacy_purchase" && value is bool) return value;
            if (field == "origin" && value is string origin && CustomerRules.Origins.Contains(origin)) return origin;
            if (field == "acquisition_source" && value is string source && CustomerRules.AcquisitionSources.Contains(source)) return source;
            if (field is "referred_by_customer_id" or "merged_into_customer_id" or "created_by" && value is Guid) return value;
            if (field == "contact_status" && value is string contactStatus && CustomerRules.ContactStatuses.Contains(contactStatus)) return contactStatus;
            if (field == "delivered_orders_count" && value is int) return value;
            if (field == "lifetime_value_vnd" && value is long) return value;
            if (field is "first_delivered_at" or "last_delivered_at" or "last_contacted_at" or "erased_at" or "created_at" or "updated_at" && value is DateTime) return value;
            return Redacted;
        }
        if (entityType == "crm.customer_addresses")
        {
            if (field == "customer_id" && value is Guid) return value;
            if (field == "province_code" && value is string provinceCode && CustomerRules.IsValidProvinceCode(provinceCode)) return provinceCode;
            if (field == "ward_code" && value is string wardCode && CustomerRules.IsValidWardCode(wardCode)) return wardCode;
            if (field == "is_default" && value is bool) return value;
            if (field is "created_at" or "updated_at" && value is DateTime) return value;
            return Redacted;
        }
        if (entityType == "crm.tags")
        {
            if (field == "color" && value is string color && CustomerRules.IsValidHexColor(color)) return color;
            return Redacted;
        }
        if (entityType == "crm.customer_accounts")
        {
            if (field == "customer_id" && value is Guid) return value;
            if (field == "status" && value is "active" or "locked" or "disabled") return value;
            if (field == "failed_login_count" && value is int) return value;
            if (field is "email_verified_at" or "phone_verified_at" or "history_unlocked_at" or "lockout_end" or "created_at" or "last_login_at" && value is DateTime) return value;
            return Redacted;
        }
        // P2-03 / NFR-04: fail-closed classification for crm.interactions; content and follow_up_result always stay redacted.
        if (entityType == "crm.interactions")
        {
            if (field is "customer_id" or "staff_id" or "order_id" or "campaign_id" && value is Guid) return value;
            if (field == "channel" && value is string channel && CustomerRules.InteractionChannels.Contains(channel)) return channel;
            if (field == "kind" && value is string kind && CustomerRules.InteractionKinds.Contains(kind)) return kind;
            if (field is "occurred_at" or "follow_up_at" or "follow_up_done_at" or "created_at" && value is DateTime) return value;
            return Redacted;
        }

        if (IsSensitive(field)) return Redacted;
        // P1-05: classify variant business fields by entity and runtime type. Names,
        // barcode, free text and arbitrary JSON remain redacted, including on creation.
        if (entityType == "catalog.variants")
        {
            if (field == "price_vnd" && value is long) return value;
            if (field == "status" && value is "active" or "inactive") return value;
            if (field == "is_unique_piece" && value is bool) return value;
            if (field is "position" or "weight_g" or "low_stock_threshold" && value is int) return value;
            if (field is "created_at" or "updated_at" && value is DateTime) return value;
            return Redacted;
        }
        // BR-PRC-03: audit the price, schedule and parent identity, with free-text notes redacted.
        if (entityType == "pricing.sale_prices")
        {
            if (field == "sale_price_vnd" && value is long) return value;
            if (field is "starts_at" or "ends_at" or "created_at" && value is DateTime) return value;
            if (field == "variant_id" && value is Guid) return value;
            return Redacted;
        }
        if (entityType is "inventory.receipts" or "inventory.stocktakes")
        {
            if (field == "status" && value is "draft" or "posted" or "counting" or "submitted" or "approved" or "cancelled") return value;
            if (field is "received_at" or "posted_at" or "submitted_at" or "approved_at" && value is DateTime) return value;
            if (field is "posted_by" or "approved_by" or "created_by" && value is Guid) return value;
            return Redacted;
        }
        if (entityType == "inventory.receipt_lines")
        {
            if (field is "receipt_id" or "variant_id" && value is Guid) return value;
            if (field == "quantity" && value is int) return value;
            return Redacted;
        }
        if (entityType == "inventory.stock_levels")
        {
            if (field is "on_hand" or "reserved" && value is int) return value;
            if (field == "updated_at" && value is DateTime) return value;
            return Redacted;
        }
        // P2-05 / NFR-04: explicit allowlist for sales.orders; all PII, addresses, notes, UTM,
        // referral_code and invoice numbers remain redacted.
        if (entityType == "sales.orders")
        {
            if (field == "code" && value is string) return value;
            if (field == "origin" && value is "web" or "admin") return value;
            if (field == "sales_channel" && value is "web" or "zalo_personal" or "zalo_oa" or "facebook" or "phone" or "in_person" or "other") return value;
            if (field == "status" && value is "pending" or "confirmed" or "shipping" or "delivered" or "delivery_failed" or "cancelled") return value;
            if (field == "payment_method" && value is "cod" or "bank_transfer") return value;
            if (field == "payment_status" && value is "unpaid" or "partially_paid" or "paid" or "partially_refunded" or "refunded") return value;
            if (field is "subtotal_vnd" or "discount_vnd" or "shipping_fee_vnd" or "total_vnd" && value is long) return value;
            if (field is "customer_id" or "voucher_id" or "created_by_staff_id" && value is Guid) return value;
            if (field is "is_first_purchase" or "is_backfilled" && value is bool) return value;
            if (field is "payment_due_at" or "placed_at" or "confirmed_at" or "shipped_at" or "delivered_at" or "failed_at" or "cancelled_at" or "created_at" or "updated_at" && value is DateTime) return value;
            return Redacted;
        }
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
