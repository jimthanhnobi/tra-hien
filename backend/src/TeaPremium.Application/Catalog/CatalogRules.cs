using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.Settings;

namespace TeaPremium.Application.Catalog;

public sealed class ProductTypeInputValidator : AbstractValidator<ProductTypeInput>
{
    public ProductTypeInputValidator()
    {
        RuleFor(x => x.Code).NotNull().WithMessage("Dữ liệu không hợp lệ.").Matches("\\A[a-z0-9_]{2,40}\\z").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.SkuPrefix).Matches("\\A[A-Z]{2,4}\\z").WithMessage("Dữ liệu không hợp lệ.").When(x => x.SkuPrefix is not null);
        RuleFor(x => x.Name).Must(CatalogRules.ValidName).WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Description).Must(CatalogRules.IsPostgresText).WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.GoogleProductCategory).Must(CatalogRules.IsPostgresText).WithMessage("Dữ liệu không hợp lệ.");
    }
}
public sealed class AttributeInputValidator : AbstractValidator<AttributeInput>
{
    public AttributeInputValidator()
    {
        RuleFor(x => x.Code).NotNull().WithMessage("Dữ liệu không hợp lệ.").Matches("\\A[a-z0-9_]{2,40}\\z").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Name).Must(CatalogRules.ValidName).WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.DataType).Must(x => x is "text" or "number" or "option" or "boolean" or "dimensions").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Scope).Must(x => x is "product" or "variant").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Unit).Must((x, unit) => unit is null || x.DataType is "number" or "dimensions").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Unit).Must(CatalogRules.IsPostgresText).WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.IsVariantAxis).Must((x, axis) => !axis || x.Scope == "variant" && x.DataType is "number" or "option" or "text").WithMessage("Dữ liệu không hợp lệ.");
    }
}
public sealed class OptionInputValidator : AbstractValidator<OptionInput>
{
    public OptionInputValidator()
    {
        RuleFor(x => x.Code).NotNull().WithMessage("Dữ liệu không hợp lệ.").Matches("\\A[a-z0-9_]{1,60}\\z").WithMessage("Dữ liệu không hợp lệ.");
        RuleFor(x => x.Label).Must(CatalogRules.ValidName).WithMessage("Dữ liệu không hợp lệ.");
    }
}
public static partial class CatalogRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    // PostgreSQL text cannot represent U+0000. Preserve other Unicode and whitespace as supplied.
    public static bool IsPostgresText(string? value) => value is null || !value.Contains('\0');
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && IsPostgresText(name);
    public static Error Invalid(string field = "request") => new("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
        new Dictionary<string, string[]> { [field] = ["Kiểm tra định dạng và giá trị."] });
    public static Error Missing => new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound);
    public static Error Conflict(string code) => new(code, "Dữ liệu xung đột; tải lại và kiểm tra trước khi lưu.", ErrorType.Conflict);
    public static Error? Validate<T>(T input, IValidator<T> validator)
    {
        var result = validator.Validate(input);
        return result.IsValid ? null : new("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
            result.Errors.GroupBy(x => JsonNamingPolicy.CamelCase.ConvertName(x.PropertyName))
                .ToDictionary(x => x.Key, _ => new List<string> { "Kiểm tra định dạng và giá trị." }.ToArray()));
    }
    public static bool ValidPage(int page, int size) => page is >= 1 and <= int.MaxValue / 100 && size is >= 1 and <= 100;
    public static string Version<T>(T value) => SettingsJson.Hash(JsonSerializer.Serialize(value, Json));
    public static Error? OptionAttribute(string dataType) => dataType == "option" ? null : Conflict("attribute_not_option");

    public static Error? SchemaChange(AttributeInput current, AttributeInput next, bool hasValues, bool hasOptions)
    {
        if (hasValues && (current.DataType != next.DataType || current.Scope != next.Scope
            || current.IsVariantAxis != next.IsVariantAxis || current.Unit != next.Unit))
            return Conflict("attribute_in_use");
        return next.DataType != "option" && hasOptions ? Conflict("attribute_has_options") : null;
    }

    // JSON merge of writable fields only; explicit null clears nullable fields, omission preserves them.
    // A hash of persisted metadata supplies concurrency without changing the P1-01 schema.
    public static Result<T> Patch<T>(T current, string version, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object || SettingsJson.HasDuplicateProperties(patch))
            return Result.Failure<T>(Invalid());
        var merged = JsonSerializer.SerializeToNode(current, Json)!.AsObject();
        if (!patch.TryGetProperty("rowVersion", out var token) || token.ValueKind != JsonValueKind.String
            || !VersionPattern().IsMatch(token.GetString()!)) return Result.Failure<T>(Invalid("rowVersion"));
        foreach (var property in patch.EnumerateObject())
        {
            if (property.Name == "rowVersion") continue;
            if (!merged.ContainsKey(property.Name)) return Result.Failure<T>(Invalid());
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        if (version != token.GetString()) return Result.Failure<T>(Conflict("concurrency_conflict"));
        try
        {
            var value = merged.Deserialize<T>(Json);
            return value is null ? Result.Failure<T>(Invalid()) : Result.Success(value);
        }
        catch (JsonException) { return Result.Failure<T>(Invalid()); }
    }
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
