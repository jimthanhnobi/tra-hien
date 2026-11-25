using System.Text.Json;

namespace TeaPremium.Application.Settings;

public static class SettingsCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["shipping.flat_fee_vnd"] = "30000",
        ["shipping.free_threshold_vnd"] = "1000000",
        ["orders.bank_transfer_hold_hours"] = "24",
        ["orders.max_quantity_per_line_web"] = "10",
        ["orders.cod_requires_call"] = "true",
        ["orders.auto_confirm_on_full_payment"] = "true",
        ["orders.max_staff_discount_percent"] = "10",
        ["returns.window_days"] = "7",
        ["marketing.min_days_between_messages"] = "14",
        ["referral.max_rewards_per_year"] = "20",
        ["payment.methods_enabled"] = "[\"cod\",\"bank_transfer\"]",
        ["referral.welcome_discount_vnd"] = "100000",
        ["referral.welcome_min_order_vnd"] = "500000",
        ["referral.reward_voucher"] = "{\"type\":\"fixed\",\"value\":100000,\"max_discount_vnd\":null,\"min_order_vnd\":500000,\"valid_days\":90}",
        ["referral.qualify_after_days"] = "7",
        ["seo.sold_unique_keep_days"] = "180",
        ["seo.handling_days"] = "{\"min\":0,\"max\":1}",
        ["seo.transit_days"] = "{\"min\":1,\"max\":5}",
        ["retention.carts_days"] = "30",
        ["retention.link_clicks_days"] = "400",
        ["retention.idempotency_hours"] = "48",
        ["retention.notification_logs_days"] = "180",
        ["retention.abandoned_upload_hours"] = "24",
        ["retention.web_vitals_days"] = "30"
    };
    private static readonly HashSet<string> ApiGroups = new(StringComparer.Ordinal)
        { "site", "shipping", "payment", "orders", "referral", "notify", "retention", "marketing" };
    private static readonly HashSet<string> SiteStrings = new(StringComparer.Ordinal)
        { "site.name", "site.hotline", "site.zalo_url", "site.facebook_url", "site.address", "site.opening_hours" };
    private static readonly HashSet<string> SiteAssets = new(StringComparer.Ordinal)
        { "site.logo_asset_id", "site.favicon_asset_id", "site.default_og_asset_id" };

    public static bool IsApiGroup(string group) => group is not null && ApiGroups.Contains(group);
    public static bool IsKnown(string key) => Defaults.ContainsKey(key) || SiteStrings.Contains(key) || SiteAssets.Contains(key)
        || key is "payment.bank" or "notify.telegram_chat_id" or "notify.shop_emails";

    public static IEnumerable<string> Validate(string group, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object || SettingsJson.HasDuplicateProperties(values))
        { yield return "Cấu hình phải là object JSON, không trùng khóa."; yield break; }
        foreach (var item in values.EnumerateObject())
            if (!IsKnown(item.Name) || !item.Name.StartsWith(group + ".", StringComparison.Ordinal) || !Valid(item.Name, item.Value))
                // Do not reflect untrusted keys/values into errors or logs.
                yield return "Khóa, nhóm, kiểu hoặc giá trị cấu hình không hợp lệ.";
    }

    private static bool Valid(string key, JsonElement value)
    {
        if (key == "shipping.free_threshold_vnd" && value.ValueKind == JsonValueKind.Null) return true;
        if (key is "orders.cod_requires_call" or "orders.auto_confirm_on_full_payment")
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (key == "payment.methods_enabled")
        {
            if (value.ValueKind != JsonValueKind.Array) return false;
            var methods = value.EnumerateArray().ToArray();
            return methods.Length is > 0 and <= 2 && methods.All(x => x.ValueKind == JsonValueKind.String && x.GetString() is "cod" or "bank_transfer")
                && methods.Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() == methods.Length;
        }
        if (key == "payment.bank") return Shape(value, ["bin", "bank_name", "account_number", "account_name"])
            && value.EnumerateObject().All(x => Text(x.Value))
            && value.GetProperty("bin").GetString() is { Length: 6 } bin && bin.All(char.IsAsciiDigit)
            && value.GetProperty("account_number").GetString()!.All(char.IsAsciiDigit);
        if (key == "referral.reward_voucher")
        {
            if (!Shape(value, ["type", "value", "max_discount_vnd", "min_order_vnd", "valid_days"])) return false;
            var type = value.GetProperty("type");
            return type.ValueKind == JsonValueKind.String && type.GetString() is "fixed" or "percent"
                && Integer(value.GetProperty("value"), 1, type.GetString() == "percent" ? 100 : long.MaxValue)
                && (value.GetProperty("max_discount_vnd").ValueKind == JsonValueKind.Null || Integer(value.GetProperty("max_discount_vnd"), 0))
                && Integer(value.GetProperty("min_order_vnd"), 0) && Integer(value.GetProperty("valid_days"), 1, int.MaxValue);
        }
        if (key is "seo.handling_days" or "seo.transit_days") return Shape(value, ["min", "max"])
            && Integer(value.GetProperty("min"), 0, int.MaxValue) && Integer(value.GetProperty("max"), 0, int.MaxValue)
            && value.GetProperty("min").GetInt64() <= value.GetProperty("max").GetInt64();
        if (SiteAssets.Contains(key)) return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty;
        if (SiteStrings.Contains(key)) return Text(value) && (!key.EndsWith("_url", StringComparison.Ordinal)
            || Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo));
        if (key == "notify.telegram_chat_id")
            return value.ValueKind == JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.String && (value.GetString()!.Length == 0 || Text(value)));
        if (key == "notify.shop_emails") return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x =>
            Text(x) && System.Net.Mail.MailAddress.TryCreate(x.GetString(), out var email) && email.Address == x.GetString());
        if (!Defaults.ContainsKey(key)) return false;
        var zeroAllowed = key.StartsWith("shipping.", StringComparison.Ordinal) || key.StartsWith("referral.welcome_", StringComparison.Ordinal)
            || key is "orders.max_staff_discount_percent" or "referral.qualify_after_days" or "marketing.min_days_between_messages";
        return Integer(value, zeroAllowed ? 0 : 1, key == "orders.max_staff_discount_percent" ? 100 : long.MaxValue);
    }
    private static bool Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString()) && !value.GetString()!.Any(char.IsControl);
    private static bool Integer(JsonElement value, long min, long max = long.MaxValue)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= min && n <= max;
    private static bool Shape(JsonElement value, string[] fields) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal));
}
