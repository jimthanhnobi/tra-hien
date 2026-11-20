namespace TeaPremium.Application.Inventory;

// P1-05 consumes read-only inventory facts. Receipt/movement/stock mutation stays in P1-08.
public interface ICatalogInventoryRead
{
    Task<bool> HasMovementsAsync(Guid variantId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> AvailableAsync(Guid[] variantIds, CancellationToken ct);
}
