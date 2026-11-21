using TeaPremium.Application.Catalog;

namespace TeaPremium.UnitTests;

public sealed class StoreCatalogQueryTests
{
    [Fact]
    public void ParsesInclusiveRangesAndStableDefaults()
    {
        var query = Assert.IsType<StoreProductQuery>(StoreCatalogQueryParser.Parse(new Dictionary<string, string[]>
        {
            ["attr.capacity_ml"] = ["180.25-245"],
            ["attr.finish"] = ["dark"],
            ["inStock"] = ["true"]
        }));
        Assert.Equal((1, 20, "newest", true), (query.Page, query.PageSize, query.Sort, query.InStock));
        Assert.Equal((180.25m, 245m), (query.Attributes[0].Min, query.Attributes[0].Max));
        Assert.Equal("dark", query.Attributes[1].Option);
    }

    [Theory]
    [InlineData("page", "0")]
    [InlineData("pageSize", "101")]
    [InlineData("priceMin", "-1")]
    [InlineData("sort", "price")]
    [InlineData("inStock", "1")]
    [InlineData("attr.capacity_ml", "10..20")]
    public void RejectsMalformedFilters(string key, string value)
    {
        Assert.Null(StoreCatalogQueryParser.Parse(new Dictionary<string, string[]> { [key] = [value] }));
        Assert.Null(StoreCatalogQueryParser.Parse(new Dictionary<string, string[]> { [key] = [value, value] }));
    }
}
