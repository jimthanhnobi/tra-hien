using System.Globalization;
using System.Net;
using System.Text;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Application.Orders;

// BR-ORD-12: pure HTML packing slip renderer with inline SVG Code 128B barcode, strict HTML encoding,
// optional hidePrices mode (hides line/summary prices while keeping COD amount), and zero cost/internalNote fields.
public static class PackingSlipHtmlRenderer
{
    private const int StartCodeB = 104;
    private const int StopCode = 106;
    private const int QuietZoneModules = 10;
    private const int BarcodeHeight = 48;

    // ISO/IEC 15417 Code 128 bar/space module patterns (values 0..105 sum to 11; 106 Stop sums to 13).
    private static readonly string[] Code128PatternTable =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    ];

    public static IReadOnlyList<string> Code128Patterns => Code128PatternTable;

    public static string Render(PackingSlipDocument doc, bool hidePrices)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!OrderCode.IsValid(doc.OrderCode))
            throw new DomainException("invalid_order_code", "Mã đơn hàng không hợp lệ để tạo mã vạch phiếu giao hàng.");

        var barcodeSvg = RenderCode128BSvg(doc.OrderCode);
        var placedVn = doc.PlacedAt.ToOffset(BusinessCalendar.VietnamOffset);
        var placedLabel = placedVn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        var sb = new StringBuilder(4096);
        sb.Append("<!DOCTYPE html><html lang=\"vi\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Phiếu giao hàng ").Append(H(doc.OrderCode)).Append("</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:system-ui,-apple-system,sans-serif;color:#111;margin:0;padding:20px;font-size:13px;line-height:1.45;}");
        sb.Append(".sheet{max-width:720px;margin:0 auto;border:1px solid #d4d4d8;padding:20px;}");
        sb.Append(".head{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;border-bottom:2px solid #111;padding-bottom:12px;margin-bottom:14px;}");
        sb.Append(".shop-name{font-size:18px;font-weight:700;margin:0 0 4px;}");
        sb.Append(".muted{color:#52525b;font-size:12px;margin:2px 0;}");
        sb.Append(".barcode{text-align:right;}");
        sb.Append(".order-code{font-family:monospace;font-size:15px;font-weight:700;letter-spacing:1px;margin-top:4px;}");
        sb.Append(".box{border:1px solid #e4e4e7;padding:10px 12px;margin-bottom:14px;}");
        sb.Append(".box-title{font-weight:700;text-transform:uppercase;font-size:11px;letter-spacing:.5px;color:#52525b;margin:0 0 6px;}");
        sb.Append("table{width:100%;border-collapse:collapse;margin-bottom:14px;}");
        sb.Append("th,td{border:1px solid #d4d4d8;padding:6px 8px;text-align:left;vertical-align:top;}");
        sb.Append("th{background:#f4f4f5;font-size:12px;}");
        sb.Append(".num{text-align:right;white-space:nowrap;}");
        sb.Append(".totals{width:280px;margin-left:auto;margin-bottom:14px;}");
        sb.Append(".totals td{border:none;padding:3px 4px;}");
        sb.Append(".totals tr.grand td{border-top:1px solid #111;font-weight:700;padding-top:6px;}");
        sb.Append(".cod-box{border:2px solid #111;padding:10px 14px;display:flex;justify-content:space-between;align-items:center;font-size:15px;font-weight:700;}");
        sb.Append("@media print{body{padding:0;}.sheet{border:none;}}");
        sb.Append("</style></head><body><main class=\"sheet\">");

        sb.Append("<header class=\"head\"><div>");
        sb.Append("<h1 class=\"shop-name\">").Append(H(string.IsNullOrWhiteSpace(doc.ShopName) ? "Trà Hiên" : doc.ShopName)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(doc.ShopHotline))
            sb.Append("<p class=\"muted\">Hotline: ").Append(H(doc.ShopHotline)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(doc.ShopAddress))
            sb.Append("<p class=\"muted\">").Append(H(doc.ShopAddress)).Append("</p>");
        sb.Append("<p class=\"muted\">Ngày đặt: ").Append(H(placedLabel)).Append(" (UTC+7)</p>");
        sb.Append("</div><div class=\"barcode\">");
        sb.Append(barcodeSvg);
        sb.Append("<div class=\"order-code\">").Append(H(doc.OrderCode)).Append("</div>");
        sb.Append("</div></header>");

        sb.Append("<section class=\"box\"><p class=\"box-title\">Người nhận</p>");
        sb.Append("<div><strong>").Append(H(doc.ReceiverName)).Append("</strong> — ").Append(H(doc.ReceiverPhone)).Append("</div>");
        sb.Append("<div>").Append(H(doc.AddressLine)).Append(", ").Append(H(doc.WardName)).Append(", ").Append(H(doc.ProvinceName)).Append("</div>");
        if (!string.IsNullOrWhiteSpace(doc.CustomerNote))
            sb.Append("<p class=\"muted\">Ghi chú khách hàng: ").Append(H(doc.CustomerNote)).Append("</p>");
        sb.Append("</section>");

        sb.Append("<table><thead><tr>");
        sb.Append("<th class=\"num\" style=\"width:40px;\">STT</th>");
        sb.Append("<th style=\"width:120px;\">SKU</th>");
        sb.Append("<th>Sản phẩm</th>");
        sb.Append("<th class=\"num\" style=\"width:56px;\">SL</th>");
        if (!hidePrices)
        {
            sb.Append("<th class=\"num\" style=\"width:110px;\">Đơn giá</th>");
            sb.Append("<th class=\"num\" style=\"width:120px;\">Thành tiền</th>");
        }
        sb.Append("</tr></thead><tbody>");

        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var line = doc.Lines[i];
            sb.Append("<tr>");
            sb.Append("<td class=\"num\">").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("<td>").Append(H(line.Sku)).Append("</td>");
            sb.Append("<td>").Append(H(line.ProductName));
            if (!string.IsNullOrWhiteSpace(line.VariantName))
                sb.Append(" — ").Append(H(line.VariantName));
            sb.Append("</td>");
            sb.Append("<td class=\"num\">").Append(line.Quantity.ToString(CultureInfo.InvariantCulture)).Append("</td>");
            if (!hidePrices)
            {
                sb.Append("<td class=\"num\">").Append(H(FormatVnd(line.UnitPriceVnd))).Append("</td>");
                sb.Append("<td class=\"num\">").Append(H(FormatVnd(line.LineTotalVnd))).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");

        if (!hidePrices)
        {
            sb.Append("<table class=\"totals\"><tbody>");
            sb.Append("<tr><td>Tạm tính</td><td class=\"num\">").Append(H(FormatVnd(doc.SubtotalVnd))).Append("</td></tr>");
            if (doc.DiscountVnd > 0)
                sb.Append("<tr><td>Giảm giá</td><td class=\"num\">-").Append(H(FormatVnd(doc.DiscountVnd))).Append("</td></tr>");
            sb.Append("<tr><td>Phí giao hàng</td><td class=\"num\">").Append(H(FormatVnd(doc.ShippingFeeVnd))).Append("</td></tr>");
            sb.Append("<tr class=\"grand\"><td>Tổng cộng</td><td class=\"num\">").Append(H(FormatVnd(doc.TotalVnd))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<section class=\"cod-box\"><span>Tiền thu hộ (COD)</span><span>")
            .Append(H(FormatVnd(doc.CodAmountVnd)))
            .Append("</span></section>");

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    internal static string RenderCode128BSvg(string code)
    {
        if (!OrderCode.IsValid(code))
            throw new DomainException("invalid_order_code", "Mã đơn hàng không hợp lệ để tạo mã vạch.");

        var symbols = new List<int>(code.Length + 3) { StartCodeB };
        var checksum = StartCodeB;
        for (var i = 0; i < code.Length; i++)
        {
            var value = code[i] - 32;
            if (value is < 0 or > 94)
                throw new DomainException("invalid_order_code", "Mã đơn hàng chứa ký tự không hỗ trợ trong mã vạch Code 128B.");
            symbols.Add(value);
            checksum += (i + 1) * value;
        }

        symbols.Add(checksum % 103);
        symbols.Add(StopCode);

        var x = QuietZoneModules;
        var rects = new StringBuilder(2048);
        foreach (var sym in symbols)
        {
            var pattern = Code128PatternTable[sym];
            for (var i = 0; i < pattern.Length; i++)
            {
                var width = pattern[i] - '0';
                if ((i & 1) == 0)
                {
                    rects.Append("<rect x=\"")
                        .Append(x.ToString(CultureInfo.InvariantCulture))
                        .Append("\" y=\"0\" width=\"")
                        .Append(width.ToString(CultureInfo.InvariantCulture))
                        .Append("\" height=\"")
                        .Append(BarcodeHeight.ToString(CultureInfo.InvariantCulture))
                        .Append("\" fill=\"#000\"/>");
                }
                x += width;
            }
        }

        var totalWidth = x + QuietZoneModules;
        return string.Concat(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ",
            totalWidth.ToString(CultureInfo.InvariantCulture),
            " ",
            BarcodeHeight.ToString(CultureInfo.InvariantCulture),
            "\" width=\"",
            (totalWidth * 2).ToString(CultureInfo.InvariantCulture),
            "\" height=\"",
            BarcodeHeight.ToString(CultureInfo.InvariantCulture),
            "\" role=\"img\" aria-label=\"",
            H(code),
            "\">",
            rects.ToString(),
            "</svg>");
    }

    internal static string FormatVnd(long amountVnd) =>
        amountVnd.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", ".", StringComparison.Ordinal) + " ₫";

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
