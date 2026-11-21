using TeaPremium.Domain.Inventory;

namespace TeaPremium.UnitTests;

public sealed class StockRulesTests
{
    [Theory]
    [InlineData(0, null, 1, 125L, 125L)]
    [InlineData(0, 99L, 1, 0L, 0L)]
    [InlineData(10, null, 1, 0L, 0L)]
    [InlineData(10, 0L, 10, 10L, 5L)]
    [InlineData(10, 100000L, 10, 130000L, 115000L)]
    [InlineData(1, 10L, 1, 11L, 11L)]
    [InlineData(2, 10L, 1, 11L, 10L)]
    [InlineData(1, 10L, 2, 11L, 11L)]
    [InlineData(int.MaxValue - 1, long.MaxValue, 1, long.MaxValue, long.MaxValue)]
    [InlineData(1_000_000_000, long.MaxValue, 1_000_000_000, long.MaxValue - 1L, long.MaxValue)]
    public void WeightedAverageUsesExactArithmetic(int onHand, long? average, int quantity, long cost, long expected) =>
        Assert.Equal(expected, StockRules.WeightedAverage(onHand, average, quantity, cost));

    [Fact]
    public void WeightedAverageRejectsOutOfRangeInputs()
    {
        Assert.Throws<OverflowException>(() => StockRules.WeightedAverage(int.MaxValue, 100L, 1, 1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockRules.WeightedAverage(-1, null, 1, 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockRules.WeightedAverage(0, null, 0, 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockRules.WeightedAverage(0, null, -1, 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockRules.WeightedAverage(0, null, 1, -1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => StockRules.WeightedAverage(0, -1L, 1, 0L));
    }

    [Theory]
    [InlineData(0, 0, false, "opening", 0, 1L, null, "invalid_stock_movement")]
    [InlineData(0, 0, false, "receipt", -1, 1L, null, "invalid_stock_movement")]
    [InlineData(0, 0, false, "receipt", 1, null, null, "invalid_stock_movement")]
    [InlineData(0, 0, false, "opening", 1, -1L, null, "invalid_stock_movement")]
    [InlineData(2, 0, false, "damage", -1, null, " x ", "invalid_stock_movement")]
    [InlineData(2, 0, false, "damage", 1, null, "damage", "invalid_stock_movement")]
    [InlineData(2, 1, false, "damage", -2, 0L, "damage", "out_of_stock")]
    [InlineData(1, 0, true, "receipt", 1, 0L, null, "unique_piece_violation")]
    [InlineData(int.MaxValue, 0, false, "receipt", 1, 0L, null, "stock_quantity_overflow")]
    public void InvalidMovementsLeaveOriginalBalance(int onHand, int reserved, bool unique, string type, int quantity,
        long? cost, string? reason, string error)
    {
        var before = new StockBalance(onHand, reserved, null);
        Assert.Equal(error, StockRules.Calculate(before, unique, new(type, quantity, cost, null, null, reason, "system", null), out var after));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 0L)]
    [InlineData(1, 5L)]
    public void ReturnWithUnknownOriginalCostPreservesExistingAverage(int onHand, long? average)
    {
        var before = new StockBalance(onHand, 0, average);
        Assert.Null(StockRules.Calculate(before, false, new("return_restock", 1, null, "return", null, null, "system", null), out var after));
        Assert.Equal(new StockBalance(onHand + 1, 0, average), after);
    }
}
