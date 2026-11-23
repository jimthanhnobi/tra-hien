namespace TeaPremium.Domain.Inventory;

public readonly record struct StockBalance(int OnHand, int Reserved, long? AvgCostVnd)
{
    public int Available => OnHand - Reserved;
}

public sealed record StockChange(string Type, int Quantity, long? UnitCostVnd, string? RefType,
    Guid? RefId, string? Reason, string ActorType, Guid? ActorId);

public static class StockRules
{
    public static bool IsLowStock(int available, int lowStockThreshold) =>
        lowStockThreshold > 0 && available <= lowStockThreshold;

    public static bool IsMovementType(string type) => type is "opening" or "receipt" or "sale" or "return_restock"
        or "adjustment" or "stocktake" or "damage" or "replacement_out";

    public static string? Calculate(StockBalance before, bool unique, StockChange change, out StockBalance after)
    {
        after = before;
        if (!Valid(before) || change.UnitCostVnd < 0 || !ValidDirection(change.Type, change.Quantity)
            || change.Type is ("opening" or "receipt") && change.UnitCostVnd is null
            || change.Type is ("adjustment" or "damage") && (change.Reason?.Trim().Length ?? 0) < 3
            || change.Reason?.Contains('\0') == true
            || change.RefType is not (null or "receipt" or "order" or "return" or "adjustment" or "stocktake" or "import")
            || change.ActorType is not ("staff" or "system")
            || change.ActorType == "staff" && (change.ActorId is null || change.ActorId == Guid.Empty))
            return "invalid_stock_movement";

        var quantity = (long)before.OnHand + change.Quantity;
        if (quantity < before.Reserved) return "out_of_stock";
        if (unique && quantity > 1) return "unique_piece_violation";
        if (quantity > int.MaxValue) return "stock_quantity_overflow";

        long? cost = before.AvgCostVnd;
        if (change.Type is ("opening" or "receipt" or "return_restock") && change.UnitCostVnd is { } incomingCost)
            cost = WeightedAverage(before.OnHand, before.AvgCostVnd, change.Quantity, incomingCost);
        after = new((int)quantity, before.Reserved, cost);
        return null;
    }

    public static long WeightedAverage(int onHand, long? averageCost, int quantity, long incomingCost)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onHand);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingCost);
        if (averageCost is { } knownCost) ArgumentOutOfRangeException.ThrowIfNegative(knownCost);

        var total = checked(onHand + quantity);
        if (onHand == 0 || averageCost is null) return incomingCost;

        // The largest numerator is bounded by int.MaxValue × long.MaxValue.
        var numerator = (Int128)onHand * averageCost.Value + (Int128)quantity * incomingCost;
        return checked((long)((numerator + total / 2) / total));
    }

    private static bool Valid(StockBalance balance) =>
        balance.Reserved >= 0 && balance.OnHand >= balance.Reserved && balance.AvgCostVnd is not < 0;

    private static bool ValidDirection(string type, int quantity) => type switch
    {
        "opening" or "receipt" or "return_restock" => quantity > 0,
        "sale" or "damage" or "replacement_out" => quantity < 0,
        "adjustment" or "stocktake" => quantity != 0,
        _ => false
    };
}
