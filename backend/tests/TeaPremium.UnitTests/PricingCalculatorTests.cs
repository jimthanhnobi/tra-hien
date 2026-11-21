using TeaPremium.Domain.Pricing;

namespace TeaPremium.UnitTests;

// BR-PRC-01/02 and BR-CAT-15.
public sealed class PricingCalculatorTests
{
    private const long UpperPrice = 10_000_000_000;
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly ScheduledPrice Promotion = new(UpperPrice - 1, Start, Start.AddMinutes(10));

    [Theory]
    [InlineData(false, 100L)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void InactiveOrMissingBasePriceIsNotSellable(bool active, long? price)
    {
        Assert.Null(PricingCalculator.Calculate(active, price, [Promotion], Start));
    }

    [Fact]
    public void NoScheduledPriceReturnsExactUpperBoundary()
    {
        Assert.Equal(new(UpperPrice, UpperPrice), PricingCalculator.Calculate(true, UpperPrice, [], Start));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public void StartInclusiveEndExclusiveAndExactVnd(int minute, bool discounted)
    {
        var time = Start.AddMinutes(minute).ToOffset(TimeSpan.FromHours(7));
        var expected = new EffectivePrice(UpperPrice, discounted ? UpperPrice - 1 : UpperPrice);
        Assert.Equal(expected, PricingCalculator.Calculate(true, UpperPrice, [Promotion], time));
    }

    [Theory]
    [InlineData(9999999999L)]
    [InlineData(9999999998L)]
    public void PromotionNeverRaisesTheReducedBasePrice(long reducedPrice)
    {
        Assert.Equal(new(reducedPrice, reducedPrice), PricingCalculator.Calculate(true, reducedPrice, [Promotion], Start));
    }

    [Fact]
    public void AdjacentPromotionTakesEffectAtTheSharedBoundary()
    {
        var next = new ScheduledPrice(UpperPrice - 2, Promotion.EndsAt, Promotion.EndsAt.AddMinutes(5));
        Assert.Equal(new(UpperPrice, next.PriceVnd), PricingCalculator.Calculate(true, UpperPrice, [Promotion, next], next.StartsAt));
    }
}
