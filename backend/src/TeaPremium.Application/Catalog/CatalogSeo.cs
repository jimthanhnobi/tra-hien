using System.Globalization;
using System.Text.RegularExpressions;

namespace TeaPremium.Application.Catalog;

// Approved BR-SEO-31/32/35; fallbacks never mutate stored explicit metadata.
public static partial class CatalogSeo
{
    public static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();
    public static string TrimWords(string value, int limit)
    {
        var normalized = Normalize(value);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = "";
        foreach (var word in words)
        {
            var candidate = result.Length == 0 ? word : result + " " + word;
            if (StringInfo.ParseCombiningCharacters(candidate).Length > limit)
                return result.Length == 0 ? word : result;
            result = candidate;
        }
        return result;
    }
    public static (string Title, string Description) Resolve(CatalogEntry entry, TeaPremium.Domain.Catalog.CatalogKind kind, string plain, string? brand)
    {
        var title = kind switch
        {
            TeaPremium.Domain.Catalog.CatalogKind.Material => entry.Name + ": đặc điểm, cách chọn và tác phẩm",
            TeaPremium.Domain.Catalog.CatalogKind.Artisan => "Nghệ nhân " + entry.Name,
            _ => entry.Name
        };
        if (!string.IsNullOrWhiteSpace(brand))
        {
            var withBrand = Normalize(title + " | " + brand);
            if (StringInfo.ParseCombiningCharacters(withBrand).Length <= 60) title = withBrand;
        }
        title = string.IsNullOrWhiteSpace(entry.SeoTitle) ? TrimWords(title, 60) : entry.SeoTitle;
        var description = kind == TeaPremium.Domain.Catalog.CatalogKind.Material && !string.IsNullOrWhiteSpace(entry.Summary) ? entry.Summary : plain;
        if (string.IsNullOrWhiteSpace(description)) description = kind switch
        {
            TeaPremium.Domain.Catalog.CatalogKind.Material => "Thông tin về chất liệu " + entry.Name + ".",
            TeaPremium.Domain.Catalog.CatalogKind.Artisan => "Thông tin về nghệ nhân " + entry.Name + ".",
            _ => "Thông tin về " + entry.Name + "."
        };
        return (title, string.IsNullOrWhiteSpace(entry.SeoDescription) ? TrimWords(description, 160) : entry.SeoDescription);
    }
    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
