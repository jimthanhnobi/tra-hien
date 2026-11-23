using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
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

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, -1, false)]
    [InlineData(0, 1, true)]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(4, 3, false)]
    public void IsLowStockRespectsDisabledZeroThresholdAndExactBoundaries(int available, int threshold, bool expected) =>
        Assert.Equal(expected, StockRules.IsLowStock(available, threshold));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"low_stock\",\"variantId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"kind\":\"unknown\",\"variantId\":\"01933000-0000-7000-8000-000000000001\"}")]
    [InlineData("{\"kind\":\"low_stock\",\"variantId\":\"01933000-0000-7000-8000-000000000001\",\"extra\":1}")]
    [InlineData("{\"kind\":\"reconciliation_mismatch\",\"variantId\":\"01933000-0000-7000-8000-000000000001\",\"vietnamDate\":\"0001-01-01\"}")]
    public void InventoryAlertTryParseRejectsMalformedPayloads(string? payload) =>
        Assert.False(InventoryAlert.TryParse(payload, out _));

    [Fact]
    public void InventoryAlertRoundTripsAndStageDeduplicatesPerVariantUsingFinalPostWriteBalance()
    {
        var variantId = Guid.CreateVersion7();
        var mismatch = new InventoryAlert(InventoryAlert.ReconciliationMismatch, variantId, new DateOnly(2026, 11, 23));
        var json = InventoryAlert.Serialize(mismatch);
        Assert.True(InventoryAlert.TryParse(json, out var parsed));
        Assert.Equal(mismatch, parsed);

        var store = new RecordingStockStore();
        var outbox = new RecordingOutboxWriter();
        var mutation = new InventoryMutation(store, outbox, new FixedClock());

        var vLow = new InventoryVariant(variantId, Guid.CreateVersion7(), "AM-TN-0001", "am-tu-sa", false, LowStockThreshold: 5);
        var pos1 = new InventoryPosition(vLow, new StockBalance(0, 0, null), false);
        Assert.Null(InventoryMutation.Plan(pos1, new("receipt", 2, 100L, "receipt", null, null, "system", null), out var post1));
        var pos2 = new InventoryPosition(vLow, post1!.BalanceAfter, true);
        Assert.Null(InventoryMutation.Plan(pos2, new("receipt", 3, 100L, "receipt", null, null, "system", null), out var post2));

        // Multi-posting for the same variant in one batch deduplicates to 1 low_stock alert (available = 5 == threshold).
        mutation.Stage([post1, post2!]);
        var alertMsg = Assert.Single(outbox.ActiveMessages, x => x.Value.Type == InventoryAlert.Message);
        Assert.True(InventoryAlert.TryParse(alertMsg.Value.Payload, out var emitted));
        Assert.Equal((InventoryAlert.LowStock, variantId), (emitted.Kind, emitted.VariantId));

        // Subsequent stage in the same transaction bringing available to threshold + 1 (6 > 5) clears the uncommitted alert.
        var pos3 = new InventoryPosition(vLow, post2!.BalanceAfter, true);
        Assert.Null(InventoryMutation.Plan(pos3, new("receipt", 1, 100L, "receipt", null, null, "system", null), out var post3));
        mutation.Stage([post3!]);
        Assert.DoesNotContain(outbox.ActiveMessages, x => x.Value.Type == InventoryAlert.Message);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 22, 18, 30, 0, TimeSpan.Zero);
    }

    private sealed class RecordingStockStore : IInventoryStockStore
    {
        public Task<IReadOnlyDictionary<string, InventoryPosition>> PreviewAsync(string[] skus, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, InventoryPosition>> LockAsync(Guid[] variantIds, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Stage(StockPosting posting, DateTime now) { }
    }

    private sealed class RecordingOutboxWriter : IOutboxWriter
    {
        public Dictionary<string, (string Type, string Payload)> ActiveMessages { get; } = new(StringComparer.Ordinal);
        private int _seq;

        public void Enqueue(string type, string payload, DateTimeOffset? availableAt = null) =>
            ActiveMessages[$"anon:{++_seq}"] = (type, payload);

        public void Enqueue(string type, string payload, string deduplicationKey, DateTimeOffset? availableAt = null) =>
            ActiveMessages[deduplicationKey] = (type, payload);

        public void ClearDeduplicated(string deduplicationKey) =>
            ActiveMessages.Remove(deduplicationKey);
    }
}
