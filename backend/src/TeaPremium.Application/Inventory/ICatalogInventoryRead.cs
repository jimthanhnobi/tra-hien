namespace TeaPremium.Application.Inventory;

// P1-05 consumes read-only inventory facts; P2-05 adds BR-SEO-21 sold evidence for unique pieces.
public interface ICatalogInventoryRead
{
    Task<int> OnHandAsync(Guid variantId, CancellationToken ct);
    Task<bool> HasMovementsAsync(Guid variantId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> AvailableAsync(Guid[] variantIds, CancellationToken ct);
    Task<IReadOnlySet<Guid>> SoldUniqueVariantIdsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct);
}
