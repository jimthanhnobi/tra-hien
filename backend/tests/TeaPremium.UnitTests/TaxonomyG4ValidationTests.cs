using System.Text.Json;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.UnitTests;

// P1-04 G4 P2-B/C / NFR-07/09: reject invalid input before starting persistence.
public sealed class TaxonomyG4ValidationTests
{
    [Theory]
    [InlineData("{\"summary\":\"a\\u0000b\"}")]
    [InlineData("{\"bioHtml\":\"a\\u0000b\"}")]
    [InlineData("{\"bodyJson\":{\"content\":[{\"text\":\"a\\u0000b\"}]}}")]
    [InlineData("{\"descriptionJson\":{\"attrs\":{\"a\\u0000b\":\"value\"}}}")]
    public void SaveRejectsNulInAllNestedStringValuesAndKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        var command = new SaveCatalog(CatalogKind.Material, null, document.RootElement, new(Guid.NewGuid(), ["catalog.write"]));
        Assert.False(new SaveCatalogValidator().Validate(command).IsValid);
    }
    [Theory]
    [InlineData(CatalogKind.Category)] [InlineData(CatalogKind.Collection)]
    [InlineData(CatalogKind.Material)] [InlineData(CatalogKind.Artisan)]
    public void QueryRejectsOnlyNulWithoutRejectingVietnameseOrWhitespace(CatalogKind kind)
    {
        var validator = new ListCatalogValidator();
        Assert.False(validator.Validate(new ListCatalog(kind, 1, 20, "a\0b", null)).IsValid);
        Assert.True(validator.Validate(new ListCatalog(kind, 1, 20, "Tử nê\n\tChu Bân", null)).IsValid);
        Assert.True(validator.Validate(new ListCatalog(kind, 1, 20, null, null)).IsValid);
    }
    [Fact]
    public void NullCollectionEntriesAreRejectedBeforeInspectingTheirProperties()
    {
        var item = new CollectionProductView(Guid.NewGuid(), 1);
        var validator = new PutCollectionProductsValidator();
        var command = new PutCollectionProducts(Guid.NewGuid(), CatalogVersions.Encode(1), [null!], new(Guid.NewGuid(), ["catalog.write"]));
        Assert.False(validator.Validate(command).IsValid);
        Assert.False(validator.Validate(command with { Items = [item, null!] }).IsValid);
        Assert.False(validator.Validate(command with { Items = [item, item] }).IsValid);
        Assert.True(validator.Validate(command with { Items = [] }).IsValid);
        Assert.True(validator.Validate(command with { Items = [item] }).IsValid);
    }
}
