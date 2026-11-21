using System.Text.Json;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Inventory;

namespace TeaPremium.Application.Inventory;

public sealed record InventoryVariant(Guid Id, Guid ProductId, string Sku, string ProductSlug, bool IsUniquePiece);
public sealed record InventoryPosition(InventoryVariant Variant, StockBalance Balance, bool HasMovements);
public sealed class StockPosting
{
    internal StockPosting(InventoryPosition position, StockChange change, StockBalance balanceAfter)
    {
        Position = position;
        Change = change;
        BalanceAfter = balanceAfter;
    }

    public InventoryPosition Position { get; }
    public StockChange Change { get; }
    public StockBalance BalanceAfter { get; }
}

public interface IInventoryStockStore
{
    Task<IReadOnlyDictionary<string, InventoryPosition>> PreviewAsync(string[] skus, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, InventoryPosition>> LockAsync(Guid[] variantIds, CancellationToken ct);
    void Stage(StockPosting posting, DateTime now);
}

// Callers lock and validate all lines inside their command UoW before staging any writes.
public sealed class InventoryMutation(IInventoryStockStore store, IOutboxWriter outbox, IClock clock)
{
    public static string? Plan(InventoryPosition position, StockChange change, out StockPosting? posting)
    {
        posting = null;
        if (change.Type == "opening" && position.HasMovements) return "opening_already_recorded";

        var error = StockRules.Calculate(position.Balance, position.Variant.IsUniquePiece, change, out var balance);
        if (error is null) posting = new(position, change, balance);
        return error;
    }

    public void Stage(IReadOnlyList<StockPosting> postings)
    {
        var now = clock.UtcNow.UtcDateTime;
        foreach (var posting in postings) store.Stage(posting, now);

        var tags = postings.Where(x => (x.Position.Balance.Available > 0) != (x.BalanceAfter.Available > 0))
            .Select(x => "product:" + x.Position.Variant.ProductSlug).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Existing handler accepts at most 200 tags, including "catalog".
        foreach (var batch in tags.Chunk(199))
            outbox.Enqueue(StorefrontInvalidate.Message, JsonSerializer.Serialize(new StorefrontInvalidate([.. batch, "catalog"])));
    }
}
