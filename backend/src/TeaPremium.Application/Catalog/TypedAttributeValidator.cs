using System.Text.Json;
using TeaPremium.Application.Results;

namespace TeaPremium.Application.Catalog;

public sealed record TypedAttributeDefinition(Guid Id, Guid ProductTypeId, string DataType, string Scope, bool IsActive);
public sealed record TypedAttributeOption(Guid Id, Guid AttributeId, bool IsActive);
public sealed record TypedAttributeValue(string? ValueText = null, decimal? ValueNumber = null,
    Guid? OptionId = null, bool? ValueBool = null, string? ValueJson = null);

// P1-03 / 03 §3.1, schema.sql ck_pav_one_value/ck_vav_one_value. P1-05 loads the
// actual definition/option under its write transaction, then calls this pure validator.
public static class TypedAttributeValidator
{
    public static Error? Validate(TypedAttributeDefinition definition, Guid productTypeId, string targetScope,
        TypedAttributeValue value, TypedAttributeOption? option = null)
    {
        if (!definition.IsActive || definition.ProductTypeId != productTypeId || definition.Scope != targetScope
            || targetScope is not ("product" or "variant")) return CatalogRules.Invalid("attribute");
        var populated = (value.ValueText is null ? 0 : 1) + (value.ValueNumber is null ? 0 : 1)
            + (value.OptionId is null ? 0 : 1) + (value.ValueBool is null ? 0 : 1) + (value.ValueJson is null ? 0 : 1);
        if (populated != 1) return CatalogRules.Invalid("value");
        var valid = definition.DataType switch
        {
            "text" => value.ValueText is not null && CatalogRules.IsPostgresText(value.ValueText),
            "number" => value.ValueNumber is { } number && number >= -9999999999.99m && number <= 9999999999.99m
                && decimal.Round(number, 2) == number,
            "option" => value.OptionId is { } id && option is not null && option.Id == id
                && option.AttributeId == definition.Id && option.IsActive,
            "boolean" => value.ValueBool.HasValue,
            "dimensions" => value.ValueJson is not null && CatalogRules.IsPostgresText(value.ValueJson) && Dimensions(value.ValueJson),
            _ => false
        };
        return valid ? null : CatalogRules.Invalid("value");
    }

    private static bool Dimensions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateObject())
            {
                if (item.Name is not ("d" or "h" or "mouth" or "l" or "w") || !keys.Add(item.Name)) return false;
                if (item.Value.ValueKind == JsonValueKind.Null) continue;
                if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetDecimal(out _)) return false;
            }
            // No required keys or minimum-size rule is specified, including for all-null/empty objects.
            return true;
        }
        catch (JsonException) { return false; }
    }
}
