#pragma warning disable CA1707 // Existing spec identifiers in test names.
using System.Text.Json;
using TeaPremium.Application.Catalog;

namespace TeaPremium.UnitTests;

public sealed class CatalogValidationTests
{
    private static readonly Guid TypeId = Guid.NewGuid();
    private static readonly Guid AttributeId = Guid.NewGuid();
    private static readonly Guid OptionId = Guid.NewGuid();
    [Theory]
    [InlineData("text", "hello", null, null, true)]
    [InlineData("text", "", null, null, true)]
    [InlineData("text", null, "1", null, false)]
    [InlineData("number", null, "9999999999.99", null, true)]
    [InlineData("number", null, "-9999999999.99", null, true)]
    [InlineData("number", null, "10000000000", null, false)]
    [InlineData("number", null, "-10000000000", null, false)]
    [InlineData("number", null, "1.001", null, false)]
    [InlineData("number", null, "0", null, true)]
    [InlineData("boolean", null, null, false, true)]
    [InlineData("boolean", null, null, true, true)]
    [InlineData("boolean", null, null, null, false)]
    [InlineData("number", "1", "1", null, false)]
    public void FR_CAT_01_ExactlyOneTypedValueAndNumeric12_2(string type, string? text, string? number, bool? boolean, bool valid)
    {
        var value = new TypedAttributeValue(text, number is null ? null : decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture), ValueBool: boolean);
        Assert.Equal(valid, TypedAttributeValidator.Validate(Definition(type), TypeId, "product", value) is null);
    }
    [Theory]
    [InlineData("{\"d\":14.3,\"h\":7.6,\"mouth\":4.6,\"l\":null,\"w\":null}", true)]
    [InlineData("{}", true)]
    [InlineData("{\"d\":null}", true)]
    [InlineData("{\"d\":\"14\"}", false)]
    [InlineData("{\"d\":true}", false)]
    [InlineData("{\"d\":[]}", false)]
    [InlineData("{\"length\":1}", false)]
    [InlineData("{\"d\":1,\"d\":2}", false)]
    [InlineData("{\"d\":NaN}", false)]
    [InlineData("{\"d\":1e1000}", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("bad-json", false)]
    public void FR_CAT_01_DimensionsShapeMatchesDDLWithoutInventingRequiredKeys(string json, bool valid)
        => Assert.Equal(valid, TypedAttributeValidator.Validate(Definition("dimensions"), TypeId, "product", new(ValueJson: json)) is null);
    [Fact]
    public void FR_CAT_01_OptionsRequireExactIdentityOwnershipAndActivity()
    {
        var value = new TypedAttributeValue(OptionId: OptionId);
        var option = new TypedAttributeOption(OptionId, AttributeId, true);
        Assert.Null(TypedAttributeValidator.Validate(Definition("option"), TypeId, "product", value, option));
        Assert.NotNull(TypedAttributeValidator.Validate(Definition("text"), TypeId, "product", value, option));
        Assert.NotNull(TypedAttributeValidator.Validate(Definition("option"), TypeId, "product", value));
        Assert.NotNull(TypedAttributeValidator.Validate(Definition("option"), TypeId, "product", value, option with { AttributeId = Guid.NewGuid() }));
        Assert.NotNull(TypedAttributeValidator.Validate(Definition("option"), TypeId, "product", value, option with { Id = Guid.NewGuid() }));
        Assert.NotNull(TypedAttributeValidator.Validate(Definition("option"), TypeId, "product", value, option with { IsActive = false }));
    }
    [Fact]
    public void FR_CAT_01_ScopeAndProductTypeMustMatch()
    {
        var definition = Definition("text"); var value = new TypedAttributeValue("present");
        Assert.NotNull(TypedAttributeValidator.Validate(definition, Guid.NewGuid(), "product", value));
        Assert.NotNull(TypedAttributeValidator.Validate(definition, TypeId, "variant", value));
        Assert.NotNull(TypedAttributeValidator.Validate(definition with { Scope = "variant" }, TypeId, "product", value));
        Assert.Null(TypedAttributeValidator.Validate(definition with { Scope = "variant" }, TypeId, "variant", value));
        Assert.NotNull(TypedAttributeValidator.Validate(definition with { IsActive = false }, TypeId, "product", value));
    }
    [Theory]
    [InlineData("UPPER", "AM", "Ấm", false)]
    [InlineData("a", null, "Ấm", false)]
    [InlineData("valid\n", null, "Ấm", false)]
    [InlineData("valid", "aM", "Ấm", false)]
    [InlineData("valid", "ABCDE", "Ấm", false)]
    [InlineData("valid", "", "Ấm", false)]
    [InlineData("valid", null, " ", false)]
    [InlineData("valid", "AM", "\u0000", false)]
    [InlineData("valid", null, "Ấm", true)]
    public void FR_CAT_01_TypeMetadataValidation(string code, string? prefix, string name, bool valid)
        => Assert.Equal(valid, new ProductTypeInputValidator().Validate(new ProductTypeInput(code, name, prefix)).IsValid);
    [Theory]
    [InlineData("text", "product", null, false, true)]
    [InlineData("boolean", "product", "cm", false, false)]
    [InlineData("dimensions", "variant", "cm", true, false)]
    [InlineData("option", "product", null, true, false)]
    [InlineData("text", "variant", null, true, true)]
    [InlineData("number", "variant", "ml", true, true)]
    [InlineData("option", "variant", null, true, true)]
    [InlineData("bad", "product", null, false, false)]
    [InlineData("text", "bad", null, false, false)]
    public void FR_CAT_01_AttributeMetadataValidation(string type, string scope, string? unit, bool axis, bool valid)
        => Assert.Equal(valid, new AttributeInputValidator().Validate(new AttributeInput("code", "Tên", type, unit, scope, axis)).IsValid);
    [Theory]
    [InlineData("{\"rowVersion\":\"bad\"}", "validation_failed")]
    [InlineData("{\"name\":\"Tên\"}", "validation_failed")]
    [InlineData("{\"rowVersion\":\"TOKEN\",\"id\":\"bad\"}", "validation_failed")]
    [InlineData("{\"rowVersion\":\"TOKEN\",\"isActive\":null}", "validation_failed")]
    [InlineData("{\"rowVersion\":\"TOKEN\",\"code\":\"a\",\"code\":\"b\"}", "validation_failed")]
    public void FR_CAT_01_PatchRejectsInvalidOrServerOwnedFields(string json, string error)
    {
        var current = new ProductTypeInput("type", "Tên"); var token = CatalogRules.Version(current);
        var patch = JsonSerializer.Deserialize<JsonElement>(json.Replace("TOKEN", token, StringComparison.Ordinal));
        Assert.Equal(error, CatalogRules.Patch(current, token, patch).Error.Code);
    }
    private static TypedAttributeDefinition Definition(string type) => new(AttributeId, TypeId, type, "product", true);
}
