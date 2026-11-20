using TeaPremium.Application.Catalog;

namespace TeaPremium.UnitTests;

// P1-05 / BR-CAT-02/14/15, AC-P1-09.
public sealed class ProductRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(10000000000L, true)]
    [InlineData(10000000001L, false)]
    public void PriceBoundsAreIntegerVnd(long? price, bool valid) => Assert.Equal(valid, ProductRules.ValidPrice(price));
    [Fact]
    public void SkuCandidatesPreserveTheFullSequenceAndBoundOnlyTheLabel()
    {
        const string basis = "AAAA-ABC-9223372036854775807";
        var values = Enumerable.Range(0, ProductRules.SkuAttempts).Select(i => ProductRules.SkuCandidate(basis, false, new string('A', 100), 2, i)).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, sku => { Assert.StartsWith(basis + "-", sku); Assert.True(sku.Length <= 40); Assert.True(ProductRules.ValidSku(sku)); });
        Assert.Equal("AM-TN-10000", ProductRules.SkuCandidate("AM-TN-10000", true, null, 1, 0));
        Assert.Equal("AM-TN-10000-V2", ProductRules.SkuCandidate("AM-TN-10000", true, null, 1, 1));
    }
    [Theory]
    [InlineData("ab", false)]
    [InlineData("A", false)]
    [InlineData("-AB", false)]
    [InlineData("AB_1", false)]
    [InlineData("AM-TN-0001", true)]
    public void ManualSkuMustAlreadyMatchTheUppercaseContract(string sku, bool valid) => Assert.Equal(valid, ProductRules.ValidSku(sku));
}
