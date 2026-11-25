using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.Notifications;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Notifications;

public sealed class NotificationOptions
{
    public const string DefaultTelegramBaseUrl = "https://api.telegram.org";

    public string TelegramBotToken { get; set; } = "";
    public string TelegramBaseUrl { get; set; } = DefaultTelegramBaseUrl;
    public int TelegramConnectTimeoutSeconds { get; set; } = 5;
    public int TelegramRequestTimeoutSeconds { get; set; } = 10;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string SmtpFromEmail { get; set; } = "";
    public string SmtpFromName { get; set; } = "Trà Hiên";
    public bool SmtpUseTls { get; set; } = true;
    public int SmtpConnectTimeoutSeconds { get; set; } = 5;
    public int SmtpSendTimeoutSeconds { get; set; } = 15;

    public string AdminBaseUrl { get; set; } = "";

    public bool IsTelegramConfigured => !string.IsNullOrWhiteSpace(TelegramBotToken);

    public bool IsSmtpConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpFromEmail);

    public TimeSpan TelegramConnectTimeout => TimeSpan.FromSeconds(TelegramConnectTimeoutSeconds);
    public TimeSpan TelegramRequestTimeout => TimeSpan.FromSeconds(TelegramRequestTimeoutSeconds);
    public TimeSpan SmtpConnectTimeout => TimeSpan.FromSeconds(SmtpConnectTimeoutSeconds);
    public TimeSpan SmtpSendTimeout => TimeSpan.FromSeconds(SmtpSendTimeoutSeconds);

    public static NotificationOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new NotificationOptions();
        configuration.GetSection("Notifications").Bind(options);

        // Structured overrides + standard infrastructure environment variables (06-infrastructure.md).
        options.TelegramBotToken = FirstNonEmpty(
            configuration["Notifications:Telegram:BotToken"],
            configuration["TELEGRAM_BOT_TOKEN"],
            options.TelegramBotToken);
        options.TelegramBaseUrl = FirstNonEmpty(
            configuration["Notifications:Telegram:BaseUrl"],
            configuration["TELEGRAM_BASE_URL"],
            options.TelegramBaseUrl,
            DefaultTelegramBaseUrl);
        options.TelegramConnectTimeoutSeconds = ParseIntOrDefault(
            configuration["Notifications:Telegram:ConnectTimeoutSeconds"],
            options.TelegramConnectTimeoutSeconds);
        options.TelegramRequestTimeoutSeconds = ParseIntOrDefault(
            configuration["Notifications:Telegram:RequestTimeoutSeconds"],
            options.TelegramRequestTimeoutSeconds);

        options.SmtpHost = FirstNonEmpty(
            configuration["Notifications:Smtp:Host"],
            configuration["SMTP_HOST"],
            options.SmtpHost);
        options.SmtpPort = ParseIntOrDefault(
            FirstNonEmpty(configuration["Notifications:Smtp:Port"], configuration["SMTP_PORT"]),
            options.SmtpPort);
        options.SmtpUsername = FirstNonEmpty(
            configuration["Notifications:Smtp:Username"],
            configuration["SMTP_USERNAME"],
            options.SmtpUsername);
        options.SmtpPassword = FirstNonEmpty(
            configuration["Notifications:Smtp:Password"],
            configuration["SMTP_PASSWORD"],
            options.SmtpPassword);
        options.SmtpFromEmail = FirstNonEmpty(
            configuration["Notifications:Smtp:FromEmail"],
            configuration["SMTP_FROM_EMAIL"],
            options.SmtpFromEmail);
        options.SmtpFromName = FirstNonEmpty(
            configuration["Notifications:Smtp:FromName"],
            configuration["SMTP_FROM_NAME"],
            options.SmtpFromName,
            "Trà Hiên");
        options.SmtpUseTls = ParseBoolOrDefault(
            FirstNonEmpty(configuration["Notifications:Smtp:UseTls"], configuration["SMTP_USE_TLS"]),
            options.SmtpUseTls);
        options.SmtpConnectTimeoutSeconds = ParseIntOrDefault(
            configuration["Notifications:Smtp:ConnectTimeoutSeconds"],
            options.SmtpConnectTimeoutSeconds);
        options.SmtpSendTimeoutSeconds = ParseIntOrDefault(
            configuration["Notifications:Smtp:SendTimeoutSeconds"],
            options.SmtpSendTimeoutSeconds);

        options.AdminBaseUrl = FirstNonEmpty(
            configuration["Notifications:AdminBaseUrl"],
            configuration["ADMIN_BASE_URL"],
            options.AdminBaseUrl);

        return options;
    }

    public static bool IsLocalEnvironment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "CI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase);

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var normalized = host.Trim();
        if (normalized.Length >= 2 && normalized[0] == '[' && normalized[^1] == ']')
            normalized = normalized[1..^1];
        return string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(normalized, "::1", StringComparison.Ordinal);
    }

    public void Validate(string environmentName)
    {
        var allowLocalInsecure = IsLocalEnvironment(environmentName);

        if (TelegramConnectTimeoutSeconds is < 1 or > 30
            || TelegramRequestTimeoutSeconds is < 1 or > 60
            || TelegramConnectTimeoutSeconds > TelegramRequestTimeoutSeconds)
            throw new InvalidOperationException("Invalid Telegram notification timeout configuration.");

        if (SmtpConnectTimeoutSeconds is < 1 or > 30
            || SmtpSendTimeoutSeconds is < 1 or > 60
            || SmtpConnectTimeoutSeconds > SmtpSendTimeoutSeconds)
            throw new InvalidOperationException("Invalid SMTP notification timeout configuration.");

        if (!Uri.TryCreate(TelegramBaseUrl.Trim(), UriKind.Absolute, out var telegramUri)
            || !string.IsNullOrEmpty(telegramUri.UserInfo)
            || !string.IsNullOrEmpty(telegramUri.Query)
            || !string.IsNullOrEmpty(telegramUri.Fragment)
            || telegramUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Invalid Telegram base URL configuration.");

        if (!allowLocalInsecure)
        {
            if (!string.Equals(telegramUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(telegramUri.Host, "api.telegram.org", StringComparison.OrdinalIgnoreCase)
                || !telegramUri.IsDefaultPort
                || telegramUri.AbsolutePath is not ("/" or ""))
                throw new InvalidOperationException("Non-local Telegram base URL must be https://api.telegram.org.");

            if (!SmtpUseTls)
                throw new InvalidOperationException("SMTP TLS is required outside Development/CI/Test environments.");
        }
        else if (string.Equals(telegramUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackHost(telegramUri.Host))
        {
            throw new InvalidOperationException("Plain HTTP Telegram base URL is allowed only for loopback hosts in Development/CI/Test.");
        }

        if (IsTelegramConfigured && TelegramBotToken.Any(char.IsControl))
            throw new InvalidOperationException("Invalid Telegram bot token configuration.");

        var anySmtpField = !string.IsNullOrWhiteSpace(SmtpHost)
            || !string.IsNullOrWhiteSpace(SmtpFromEmail)
            || !string.IsNullOrWhiteSpace(SmtpUsername)
            || !string.IsNullOrWhiteSpace(SmtpPassword);

        if (anySmtpField)
        {
            if (string.IsNullOrWhiteSpace(SmtpHost) || SmtpHost.Any(char.IsControl) || SmtpHost.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Invalid SMTP host configuration.");
            if (SmtpPort is < 1 or > 65535)
                throw new InvalidOperationException("Invalid SMTP port configuration.");
            if (SmtpPort == 465)
                throw new InvalidOperationException("SMTP port 465 (implicit TLS) is not supported in P2-13; use STARTTLS port 587.");
            if (!SmtpUseTls && !IsLoopbackHost(SmtpHost))
                throw new InvalidOperationException("Non-TLS SMTP is allowed only for loopback hosts in Development/CI/Test.");
            if (!EmailSecurity.IsValidEmailAddress(SmtpFromEmail.Trim()))
                throw new InvalidOperationException("Invalid SMTP sender address configuration.");
            if (string.IsNullOrWhiteSpace(SmtpFromName) || SmtpFromName.Any(char.IsControl))
                throw new InvalidOperationException("Invalid SMTP sender name configuration.");
            var hasUser = !string.IsNullOrWhiteSpace(SmtpUsername);
            var hasPass = !string.IsNullOrWhiteSpace(SmtpPassword);
            if (hasUser != hasPass)
                throw new InvalidOperationException("SMTP username and password must both be set or both empty.");
            if (hasUser && (SmtpUsername.Any(char.IsControl) || SmtpPassword.Any(char.IsControl)))
                throw new InvalidOperationException("Invalid SMTP credential configuration.");
        }

        if (!string.IsNullOrWhiteSpace(AdminBaseUrl))
        {
            if (!Uri.TryCreate(AdminBaseUrl.Trim(), UriKind.Absolute, out var adminUri)
                || !string.IsNullOrEmpty(adminUri.UserInfo)
                || !string.IsNullOrEmpty(adminUri.Query)
                || !string.IsNullOrEmpty(adminUri.Fragment)
                || adminUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Invalid AdminBaseUrl notification configuration.");
            if (!allowLocalInsecure && !string.Equals(adminUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Non-local AdminBaseUrl must use HTTPS.");
        }
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }
        return "";
    }

    private static int ParseIntOrDefault(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ParseBoolOrDefault(string? raw, bool fallback) =>
        bool.TryParse(raw, out var parsed) ? parsed : fallback;
}

internal sealed record TelegramSettingState(string? ChatId, DateTime? UpdatedAtUtc)
{
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ChatId);
}

internal sealed record ShopEmailsSettingState(IReadOnlyList<string> Emails, DateTime? UpdatedAtUtc)
{
    public bool IsEnabled => Emails.Count > 0;
}

internal static class NotificationSettingsReader
{
    public const string TelegramChatIdKey = "notify.telegram_chat_id";
    public const string ShopEmailsKey = "notify.shop_emails";

    public static async Task<TelegramSettingState> ReadTelegramAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking()
            .Where(x => x.Key == TelegramChatIdKey)
            .Select(x => new { x.Value, x.UpdatedAt })
            .SingleOrDefaultAsync(ct);

        if (row is null) return new(null, null);
        return new(ParseTelegramChatId(row.Value), row.UpdatedAt);
    }

    public static async Task<ShopEmailsSettingState> ReadShopEmailsAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking()
            .Where(x => x.Key == ShopEmailsKey)
            .Select(x => new { x.Value, x.UpdatedAt })
            .SingleOrDefaultAsync(ct);

        if (row is null) return new([], null);
        return new(ParseShopEmails(row.Value), row.UpdatedAt);
    }

    public static async Task<(TelegramSettingState Telegram, ShopEmailsSettingState ShopEmails)> ReadNotifyGroupAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(x => x.Key == TelegramChatIdKey || x.Key == ShopEmailsKey)
            .Select(x => new { x.Key, x.Value, x.UpdatedAt })
            .ToListAsync(ct);

        TelegramSettingState telegram = new(null, null);
        ShopEmailsSettingState emails = new([], null);

        foreach (var row in rows)
        {
            if (row.Key == TelegramChatIdKey)
                telegram = new(ParseTelegramChatId(row.Value), row.UpdatedAt);
            else if (row.Key == ShopEmailsKey)
                emails = new(ParseShopEmails(row.Value), row.UpdatedAt);
        }

        return (telegram, emails);
    }

    internal static string? ParseTelegramChatId(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.String) return null;
            var value = doc.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return null;
            return NotificationContracts.NormalizeTelegramChatId(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> ParseShopEmails(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var raw = new List<string?>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    raw.Add(item.GetString());
            }
            return NotificationContracts.DeduplicateShopEmails(raw);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
