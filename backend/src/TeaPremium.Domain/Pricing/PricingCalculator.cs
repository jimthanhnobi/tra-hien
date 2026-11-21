namespace TeaPremium.Domain.Pricing;

public sealed record ScheduledPrice(long PriceVnd, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record EffectivePrice(long BasePriceVnd, long EffectivePriceVnd);

public static class PricingCalculator
{
    // BR-PRC-01/02, BR-CAT-15. The caller supplies the variant's nonoverlapping schedule.
    public static EffectivePrice? Calculate(bool active, long? basePriceVnd, IEnumerable<ScheduledPrice> promotions, DateTimeOffset at)
    {
        if (!active || basePriceVnd is not { } basePrice) return null;

        var promotion = promotions.FirstOrDefault(x => x.StartsAt <= at && at < x.EndsAt);
        return new(basePrice, promotion is null ? basePrice : Math.Min(basePrice, promotion.PriceVnd));
    }
}
