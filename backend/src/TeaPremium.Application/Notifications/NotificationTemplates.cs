using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace TeaPremium.Application.Notifications;

public sealed record RenderedShopNotification(
    string TelegramHtml,
    string EmailSubject,
    string EmailPlainText,
    string EmailHtml);

public static class TelegramHtml
{
    public static string Escape(string? value) => EscapeCore(value, allowNewLines: false);

    public static string EscapeMultiline(string? value) => EscapeCore(value, allowNewLines: true);

    private static string EscapeCore(string? value, bool allowNewLines)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                if (allowNewLines && c == '\n') sb.Append('\n');
                continue;
            }
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

public static class TelegramMarkdownV2
{
    // Reserved characters per Telegram Bot API MarkdownV2 specification plus backslash.
    private static readonly HashSet<char> Reserved =
    [
        '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!', '\\'
    ];

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                if (c == '\n') sb.Append('\n');
                continue;
            }
            if (Reserved.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

public static class EmailSecurity
{
    // Matches schema.sql:563 (^[^@\s]+@[^@\s]+\.[^@\s]+$) and blocks whitespace, angle brackets, quotes, and controls.
    public static bool IsValidEmailAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
            return false;

        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"')
                return false;
        }

        var atIndex = value.IndexOf('@');
        if (atIndex <= 0 || atIndex != value.LastIndexOf('@'))
            return false;

        var domain = value.AsSpan(atIndex + 1);
        var dotIndex = domain.IndexOf('.');
        if (dotIndex <= 0 || domain[0] == '.' || domain[^1] == '.' || domain.Contains("..", StringComparison.Ordinal))
            return false;

        return MailAddress.TryCreate(value, out var parsed)
            && string.Equals(parsed.Address, value, StringComparison.Ordinal);
    }

    public static string ValidateHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new NotificationDeliveryException("invalid_email_header");
        return value.Trim();
    }

    public static string ValidateRecipient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new NotificationDeliveryException("invalid_email_recipient");
        var trimmed = value.Trim();
        if (!IsValidEmailAddress(trimmed))
            throw new NotificationDeliveryException("invalid_email_recipient");
        return trimmed;
    }
}

// BR-PLT-03: Vietnamese notification templates stored in code.
public static class NotificationTemplates
{
    private static readonly HtmlEncoder VietnameseHtmlEncoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly NumberFormatInfo VndNumberFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalDigits = 0
    };

    public static string FormatVnd(long amountVnd) =>
        amountVnd.ToString("N0", VndNumberFormat) + " đ";

    public static string PaymentMethodLabel(string paymentMethod) => paymentMethod switch
    {
        "cod" => "COD (thu hộ)",
        "bank_transfer" => "Chuyển khoản",
        _ => "Khác"
    };

    public static string? BuildSafeAdminLink(string? adminBaseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(adminBaseUrl) || string.IsNullOrWhiteSpace(relativePath))
            return null;
        if (!relativePath.StartsWith('/') || relativePath.Any(char.IsControl))
            return null;
        if (!Uri.TryCreate(adminBaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            return null;
        var root = baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/');
        return root + relativePath;
    }

    // BR-PLT-03 order_placed_shop: only order code, total VND, payment method, item count, Admin link.
    // Strictly no phone, address, customer name, customer email, notes, artisan name or cost.
    public static RenderedShopNotification RenderOrderPlacedShop(OrderPlacedNotification order, string? adminBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(order);
        var safeCode = SanitizeSingleLine(order.OrderCode);
        var totalText = FormatVnd(order.TotalVnd);
        var paymentLabel = PaymentMethodLabel(order.PaymentMethod);
        var adminLink = BuildSafeAdminLink(adminBaseUrl, $"/orders/{order.OrderId:D}");

        var tg = new StringBuilder(256);
        tg.Append("<b>Đơn hàng mới #").Append(TelegramHtml.Escape(safeCode)).Append("</b>\n");
        tg.Append("• Tổng tiền: <b>").Append(TelegramHtml.Escape(totalText)).Append("</b>\n");
        tg.Append("• Thanh toán: ").Append(TelegramHtml.Escape(paymentLabel)).Append('\n');
        tg.Append("• Số món: ").Append(order.ItemCount.ToString(CultureInfo.InvariantCulture));
        if (adminLink is not null)
            tg.Append("\n• <a href=\"").Append(TelegramHtml.Escape(adminLink)).Append("\">Mở đơn trong Admin</a>");

        var subject = EmailSecurity.ValidateHeaderValue($"Trà Hiên — Đơn hàng mới #{safeCode}");

        var plain = new StringBuilder(256);
        plain.Append("Đơn hàng mới #").AppendLine(safeCode);
        plain.Append("Tổng tiền: ").AppendLine(totalText);
        plain.Append("Phương thức thanh toán: ").AppendLine(paymentLabel);
        plain.Append("Số món: ").AppendLine(order.ItemCount.ToString(CultureInfo.InvariantCulture));
        if (adminLink is not null)
            plain.Append("Xem chi tiết trong Admin: ").AppendLine(adminLink);

        var enc = VietnameseHtmlEncoder;
        var html = new StringBuilder(512);
        html.Append("<!DOCTYPE html><html lang=\"vi\"><body>");
        html.Append("<h2>Đơn hàng mới #").Append(enc.Encode(safeCode)).Append("</h2>");
        html.Append("<ul>");
        html.Append("<li><strong>Tổng tiền:</strong> ").Append(enc.Encode(totalText)).Append("</li>");
        html.Append("<li><strong>Phương thức thanh toán:</strong> ").Append(enc.Encode(paymentLabel)).Append("</li>");
        html.Append("<li><strong>Số món:</strong> ").Append(order.ItemCount.ToString(CultureInfo.InvariantCulture)).Append("</li>");
        html.Append("</ul>");
        if (adminLink is not null)
            html.Append("<p><a href=\"").Append(enc.Encode(adminLink)).Append("\">Mở đơn trong Admin</a></p>");
        html.Append("</body></html>");

        return new(tg.ToString(), subject, plain.ToString().TrimEnd(), html.ToString());
    }

    // BR-PLT-03 daily_digest_shop (08:00 Asia/Ho_Chi_Minh, Telegram only, zero PII/artisan/cost).
    public static string RenderDailyDigestShop(DailyDigestSnapshot snapshot, string? adminBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var dateLabel = snapshot.VietnamDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(1024);
        sb.Append("<b>Tổng hợp vận hành ngày ").Append(dateLabel).Append(" (08:00)</b>\n");
        sb.Append("• Đơn chờ xác nhận: <b>").Append(snapshot.PendingConfirmationOrders.ToString(CultureInfo.InvariantCulture)).Append("</b>\n");
        sb.Append("• Chờ thanh toán sắp hết hạn (24h): <b>").Append(snapshot.ExpiringPaymentOrders.ToString(CultureInfo.InvariantCulture)).Append("</b>\n");
        sb.Append("• COD chưa đối soát: <b>").Append(snapshot.UnsettledCodCount.ToString(CultureInfo.InvariantCulture)).Append("</b>");
        if (snapshot.UnsettledCodCount > 0)
            sb.Append(" (").Append(TelegramHtml.Escape(FormatVnd(snapshot.UnsettledCodTotalVnd))).Append(')');
        sb.Append('\n');
        sb.Append("• Hẹn chăm sóc hôm nay: <b>").Append(snapshot.FollowUpsDueToday.ToString(CultureInfo.InvariantCulture)).Append("</b>\n");
        sb.Append("• Tồn kho thấp: <b>").Append(snapshot.LowStockTotal.ToString(CultureInfo.InvariantCulture)).Append(" SKU</b>");

        var maxItems = Math.Min(snapshot.LowStockItems.Count, NotificationContracts.DailyDigestLowStockMaxItems);
        var shown = 0;
        for (var i = 0; i < maxItems; i++)
        {
            var item = snapshot.LowStockItems[i];
            var line = $"\n  - <code>{TelegramHtml.Escape(SanitizeSingleLine(item.Sku))}</code>: {item.Available.ToString(CultureInfo.InvariantCulture)}/{item.LowStockThreshold.ToString(CultureInfo.InvariantCulture)}";
            if (sb.Length + line.Length + 256 >= NotificationContracts.TelegramMaxMessageLength)
                break;
            sb.Append(line);
            shown++;
        }

        if (snapshot.LowStockTotal > shown)
        {
            var remaining = snapshot.LowStockTotal - shown;
            sb.Append("\n  ... và ").Append(remaining.ToString(CultureInfo.InvariantCulture)).Append(" SKU khác");
        }

        if (snapshot.ReconciliationMismatchTotal > 0)
        {
            sb.Append("\n• Lệch đối soát sổ kho: <b>")
              .Append(snapshot.ReconciliationMismatchTotal.ToString(CultureInfo.InvariantCulture))
              .Append(" SKU</b>");
        }

        sb.Append("\n• Outbox dead: <b>").Append(snapshot.DeadOutboxCount.ToString(CultureInfo.InvariantCulture)).Append("</b>");

        var adminLink = BuildSafeAdminLink(adminBaseUrl, "/inventory");
        if (adminLink is not null)
        {
            var linkLine = $"\n• <a href=\"{TelegramHtml.Escape(adminLink)}\">Mở trang quản trị</a>";
            if (sb.Length + linkLine.Length < NotificationContracts.TelegramMaxMessageLength)
                sb.Append(linkLine);
        }

        return sb.ToString();
    }

    // GAP-03 3A immediate Telegram alert for active reconciliation_mismatch (zero PII/product/artisan/cost).
    public static string RenderInventoryReconciliationShop(InventoryReconciliationAlertModel alert, string? adminBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var delta = alert.LedgerQuantity - alert.OnHand;
        var deltaText = delta > 0
            ? "+" + delta.ToString(CultureInfo.InvariantCulture)
            : delta.ToString(CultureInfo.InvariantCulture);
        var dateLabel = alert.VietnamDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        var sb = new StringBuilder(256);
        sb.Append("<b>Cảnh báo lệch đối soát sổ kho (").Append(dateLabel).Append(")</b>\n");
        sb.Append("• SKU: <code>").Append(TelegramHtml.Escape(SanitizeSingleLine(alert.Sku))).Append("</code>\n");
        sb.Append("• Tồn hiện tại (on_hand): ").Append(alert.OnHand.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("• Tổng sổ kho (ledger): ").Append(alert.LedgerQuantity.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("• Chênh lệch: <b>").Append(TelegramHtml.Escape(deltaText)).Append("</b>");
        var reservedDelta = alert.ActiveReservationQuantity - alert.Reserved;
        if (reservedDelta != 0)
        {
            var reservedDeltaText = reservedDelta > 0
                ? "+" + reservedDelta.ToString(CultureInfo.InvariantCulture)
                : reservedDelta.ToString(CultureInfo.InvariantCulture);
            sb.Append("\n• Giữ hàng (reserved): ").Append(alert.Reserved.ToString(CultureInfo.InvariantCulture))
              .Append(" / active reservations: ").Append(alert.ActiveReservationQuantity.ToString(CultureInfo.InvariantCulture))
              .Append(" (lệch <b>").Append(TelegramHtml.Escape(reservedDeltaText)).Append("</b>)");
        }
        var adminLink = BuildSafeAdminLink(adminBaseUrl, "/inventory");
        if (adminLink is not null)
            sb.Append("\n• <a href=\"").Append(TelegramHtml.Escape(adminLink)).Append("\">Kiểm tra cảnh báo kho</a>");
        return sb.ToString();
    }

    // GAP-03 3A immediate Telegram alert for non-NotificationDelivery outbox dead-letter (zero payload/PII/secrets).
    public static string RenderOutboxDeadShop(OutboxDeadAlertModel alert, string? adminBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var sb = new StringBuilder(256);
        sb.Append("<b>Sự cố Outbox chuyển dead</b>\n");
        sb.Append("• Loại message: <code>").Append(TelegramHtml.Escape(SanitizeSingleLine(alert.MessageType))).Append("</code>\n");
        sb.Append("• Mã message: <code>").Append(alert.MessageId.ToString("D")).Append("</code>\n");
        sb.Append("• Số lần thử: ").Append(alert.Attempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("• Mã lỗi: <code>").Append(TelegramHtml.Escape(SanitizeSingleLine(alert.ErrorCode))).Append("</code>");
        var adminLink = BuildSafeAdminLink(adminBaseUrl, "/system/outbox");
        if (adminLink is not null)
            sb.Append("\n• <a href=\"").Append(TelegramHtml.Escape(adminLink)).Append("\">Xem Sự cố hệ thống</a>");
        return sb.ToString();
    }

    private static string SanitizeSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
