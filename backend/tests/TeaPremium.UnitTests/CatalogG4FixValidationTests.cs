using TeaPremium.Application.Catalog;

namespace TeaPremium.UnitTests;

// P1-03 / FR-CAT-01 / G4 P1 + P2: unit semantics and PostgreSQL text boundaries.
public sealed class CatalogG4FixValidationTests
{
    [Theory]
    [InlineData(null, "ml", true, false)]
    [InlineData("ml", null, true, false)]
    [InlineData("ml", "l", true, false)]
    [InlineData("ml", "ml", true, true)]
    [InlineData(null, null, true, true)]
    [InlineData(null, "ml", false, true)]
    [InlineData("ml", null, false, true)]
    [InlineData("ml", "l", false, true)]
    public void UnitChangeDependsOnPersistedValues(string? before, string? after, bool hasValues, bool allowed)
    {
        var current = new AttributeInput("capacity", "Dung tích", "number", before);
        var next = current with { Unit = after, Name = "Dung tích mới", IsFilterable = true };
        var error = CatalogRules.SchemaChange(current, next, hasValues, false);
        Assert.Equal(allowed, error is null);
        if (!allowed) Assert.Equal("attribute_in_use", error!.Code);
    }

    [Theory]
    [InlineData("Ấm tử sa\n180 ml\tđúng chuẩn", true)]
    [InlineData("literal \\u0000", true)]
    [InlineData("", true)]
    [InlineData("\0", false)]
    [InlineData("Ấm\0tử sa", false)]
    [InlineData("180 ml\0", false)]
    public void TypedTextRejectsNulWithoutStrippingUnicodeOrWhitespace(string text, bool allowed)
    {
        var typeId = Guid.NewGuid();
        var definition = new TypedAttributeDefinition(Guid.NewGuid(), typeId, "text", "product", true);
        var value = new TypedAttributeValue(ValueText: text);
        Assert.Equal(allowed, TypedAttributeValidator.Validate(definition, typeId, "product", value) is null);
        Assert.Equal(text, value.ValueText);
    }

    [Theory]
    [InlineData("{\"d\":\"\\u0000\"}")]
    [InlineData("{\"\\u0000\":1}")]
    [InlineData("{\"d\":{\"nested\":\"\\u0000\"}}")]
    [InlineData("{\"d\":\"\0\"}")]
    public void DimensionsCannotCarryRawOrEscapedNestedNul(string json)
    {
        var typeId = Guid.NewGuid();
        var definition = new TypedAttributeDefinition(Guid.NewGuid(), typeId, "dimensions", "product", true);
        Assert.NotNull(TypedAttributeValidator.Validate(definition, typeId, "product", new(ValueJson: json)));
    }

    [Fact]
    public void OptionalMetadataAcceptsNullAndVietnameseWhitespaceButRejectsNul()
    {
        var type = new ProductTypeInput("teapot", "Ấm\ttrà", Description: "Mô tả\nđủ dấu", GoogleProductCategory: null);
        var validator = new ProductTypeInputValidator();
        Assert.True(validator.Validate(type).IsValid);
        Assert.False(validator.Validate(type with { Description = "Mô\0tả" }).IsValid);
        Assert.False(validator.Validate(type with { GoogleProductCategory = "Gốm\0sứ" }).IsValid);
        Assert.True(new AttributeInputValidator().Validate(new AttributeInput("capacity", "Dung tích", "number", null)).IsValid);
        Assert.False(new AttributeInputValidator().Validate(new AttributeInput("capacity", "Dung tích", "number", "m\0l")).IsValid);
        Assert.True(new OptionInputValidator().Validate(new OptionInput("luoi_loc", "Lưới lọc\n7 lỗ")).IsValid);
    }
}
