using System.Globalization;
using System.Text.RegularExpressions;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;

public static partial class StoreCatalogQueryParser
{
    public static StoreProductQuery? Parse(IReadOnlyDictionary<string, string[]> query)
    {
        var attributes = new List<StoreAttributeFilter>();
        foreach (var (key, values) in query)
        {
            if (values.Length != 1 || values[0].Length > 200 || values[0].Contains('\0')) return null;
            if (!key.StartsWith("attr.", StringComparison.OrdinalIgnoreCase)) continue;
            var code = key[5..];
            var value = values[0];
            if (!CatalogRules.ReferenceCode().IsMatch(code)) return null;
            var range = Range().Match(value);
            if (range.Success)
            {
                if (!decimal.TryParse(range.Groups[1].Value, CultureInfo.InvariantCulture, out var low) ||
                    !decimal.TryParse(range.Groups[2].Value, CultureInfo.InvariantCulture, out var high))
                    return null;
                attributes.Add(new(code, null, low, high));
            }
            else if (CatalogRules.OptionCode().IsMatch(value)) attributes.Add(new(code, value, null, null));
            else return null;
        }

        string? Get(string key) => query.TryGetValue(key, out var value) ? value[0] : null;
        var sort = Get("sort") ?? "newest";
        var stock = Get("inStock");
        if (sort is not ("newest" or "price_asc" or "price_desc") || stock is not (null or "true" or "false") ||
            attributes.Count > 20 ||
            !Number(Get("page"), 1, int.MaxValue, out var page) || !Number(Get("pageSize"), 20, 100, out var size) ||
            !Price(Get("priceMin"), out var min) || !Price(Get("priceMax"), out var max) || min > max)
            return null;
        var category = Get("category");
        var collection = Get("collection");
        var material = Get("material");
        var artisan = Get("artisan");
        var type = Get("type");
        if (!ValidSlugFilter(category) || !ValidSlugFilter(collection) || !ValidSlugFilter(material) || !ValidSlugFilter(artisan)
            || type is not null && !CatalogRules.ReferenceCode().IsMatch(type)) return null;
        return new(category, collection, type, material, artisan, min, max,
            stock == "true", Get("q"), sort, page, size, attributes.ToArray());
    }

    private static bool ValidSlugFilter(string? value) => value is null || TaxonomyRules.ValidSlug(value);

    private static bool Number(string? value, int fallback, int max, out int result)
    {
        result = fallback;
        return value is null || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= 1 && result <= max;
    }

    private static bool Price(string? value, out long? result)
    {
        result = null;
        if (value is null) return true;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var price)) return false;
        result = price;
        return true;
    }

    [GeneratedRegex(@"\A(-?[0-9]{1,10}(?:\.[0-9]{1,2})?)-(-?[0-9]{1,10}(?:\.[0-9]{1,2})?)\z")]
    private static partial Regex Range();
}
