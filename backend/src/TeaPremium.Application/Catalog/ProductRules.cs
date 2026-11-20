using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;

// BR-CAT-02/04/14/15; pure rules shared by the aggregate workflow and persistence allocation.
public static partial class ProductRules
{
    public const int SkuAttempts = 64;
    public static Error Bad(string field, string code = "validation_failed") => new(code, "Dữ liệu không hợp lệ.", ErrorType.Validation,
        new Dictionary<string, string[]> { [field] = [code] });
    public static bool ValidPrice(long? price) => price is >= 1 and <= 10000000000;
    public static bool ValidSku(string sku) => SkuPattern().IsMatch(sku);
    public static string SkuCandidate(string basis, bool first, string? label, int ordinal, int attempt)
    {
        if (first && attempt == 0) return basis;
        var sequence = "V" + (ordinal + attempt).ToString(CultureInfo.InvariantCulture);
        var normalized = TaxonomyRules.Slug(label ?? "").ToUpperInvariant();
        var suffix = attempt == 0 && normalized.Length > 0 ? normalized : sequence;
        if (attempt > 0 && normalized.Length > 0) suffix = normalized + "-" + sequence;
        var budget = 40 - basis.Length - 1;
        if (suffix.Length > budget)
        {
            var tail = "-" + sequence;
            var head = normalized[..Math.Min(normalized.Length, Math.Max(0, budget - tail.Length))].Trim('-');
            suffix = head.Length == 0 ? sequence : head + tail;
        }
        return basis + "-" + suffix;
    }
    public static bool Apply(object target, JsonElement fields, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var field in fields.EnumerateObject())
            {
                if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal)) return false;
                if (field.Name is "rowVersion" or "noindexReason" or "confirmTypeChange" or "defaultVariant") continue;
                var property = target.GetType().GetProperty(char.ToUpperInvariant(field.Name[0]) + field.Name[1..], BindingFlags.Public | BindingFlags.Instance);
                if (property is null) continue; // explicitly allowed command controls, never persisted by reflection
                if (field.Value.ValueKind == JsonValueKind.Null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null) return false;
                object? value = property.PropertyType == typeof(string) && field.Name is "descriptionJson" or "packageDimsCm"
                    ? field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.ValueKind == JsonValueKind.Object ? field.Value.GetRawText() : throw new JsonException()
                    : field.Value.Deserialize(property.PropertyType, JsonOptions);
                property.SetValue(target, value);
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static List<string> Values(IReadOnlyList<ProductValue> values, ProductReference reference, string scope)
    {
        var errors = new List<string>(); var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (value is null || !seen.Add(value.AttributeId)) { errors.Add("attribute"); continue; }
            var definition = reference.Attributes.SingleOrDefault(x => x.Id == value.AttributeId);
            if (definition is null || TypedAttributeValidator.Validate(new(definition.Id, definition.ProductTypeId, definition.DataType, definition.Scope, definition.IsActive),
                reference.TypeId, scope, value.Typed(), reference.Options.SingleOrDefault(x => x.Id == value.OptionId)) is not null) errors.Add(value.AttributeId.ToString());
        }
        return errors;
    }
    public static Dictionary<string, string[]> PublishErrors(ProductData p, ProductReference reference, bool categoryVisible, bool coverValid)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string field, string code) => errors[field] = errors.TryGetValue(field, out var old) ? [.. old, code] : [code];
        if (string.IsNullOrWhiteSpace(p.Name)) Add("name", "product_name_missing");
        if (string.IsNullOrWhiteSpace(p.ShortDescription)) Add("shortDescription", "short_description_missing");
        if (string.IsNullOrWhiteSpace(p.DescriptionHtml)) Add("descriptionHtml", "description_missing");
        if (!categoryVisible || p.PrimaryCategoryId is not { } category || !p.CategoryIds.Contains(category)) Add("primaryCategoryId", "primary_category_invalid");
        if (!coverValid) Add("media.cover.altText", "cover_alt_missing");
        var active = p.Variants.Where(x => x.Status == "active").ToArray();
        if (active.Length == 0) Add("variants", "active_variant_missing");
        foreach (var v in active) if (!ValidPrice(v.PriceVnd)) Add($"variants[{v.Id}].priceVnd", v.PriceVnd is null ? "variant_price_missing" : "variant_price_invalid");
        foreach (var attr in reference.Attributes.Where(x => x.IsActive && x.IsRequiredForPublish))
        {
            if (attr.Scope == "product" && !HasValue(p.Attributes, attr.Id)) Add($"attributes[{attr.Id}]", "required_attribute_missing");
            if (attr.Scope == "variant") foreach (var v in active) if (!HasValue(v.Attributes, attr.Id)) Add($"variants[{v.Id}].attributes[{attr.Id}]", "required_attribute_missing");
        }
        foreach (var error in VariantAxisErrors(p, reference)) errors[error.Key] = errors.TryGetValue(error.Key, out var prior) ? [.. prior, .. error.Value] : error.Value;
        if (string.IsNullOrWhiteSpace(p.SeoTitle) || p.SeoTitle.Length > 70) Add("seoTitle", "seo_title_invalid");
        if (p.SeoDescription is not { Length: >= 50 and <= 160 }) Add("seoDescription", "seo_description_invalid");
        return errors;
    }
    // BR-CAT-04.6 applies to the final active aggregate as well as the publish transition.
    public static Dictionary<string, string[]> VariantAxisErrors(ProductData p, ProductReference reference)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string field, string code) => errors[field] = errors.TryGetValue(field, out var old) ? [.. old, code] : [code];
        var active = p.Variants.Where(x => x.Status == "active").ToArray();
        var axes = reference.Attributes.Where(x => x.IsActive && x.IsVariantAxis).OrderBy(x => x.Id).ToArray();
        var combinations = new HashSet<string>(StringComparer.Ordinal);
        if (active.Length > 1) foreach (var v in active)
        {
            foreach (var axis in axes) if (!HasValue(v.Attributes, axis.Id)) Add($"variants[{v.Id}].attributes[{axis.Id}]", "variant_axis_missing");
            var key = JsonSerializer.Serialize(axes.Select(a =>
            {
                var value = v.Attributes.SingleOrDefault(x => x.AttributeId == a.Id);
                return value?.ValueNumber?.ToString("G29", CultureInfo.InvariantCulture) ?? value?.OptionId?.ToString("N") ?? value?.ValueText;
            }).ToArray());
            if (!combinations.Add(key)) Add("variants", "variant_axis_duplicate");
        }
        return errors;
    }
    // Match the published optional nullable control types; omission and null do not grant confirmation.
    public static bool ValidControlFields(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in fields.EnumerateObject())
        {
            if (field.Name is "noindexReason" or "rowVersion" && field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            if (field.Name == "confirmTypeChange" && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
            if (field.Name == "defaultVariant" && !ValidControlFields(field.Value)) return false;
        }
        return true;
    }
    private static bool HasValue(IEnumerable<ProductValue> values, Guid id) => values.Any(x => x.AttributeId == id && (x.ValueText is null || !string.IsNullOrWhiteSpace(x.ValueText)));
    [GeneratedRegex("\\A[A-Z0-9][A-Z0-9-]{1,39}\\z", RegexOptions.CultureInvariant)] private static partial Regex SkuPattern();
}
