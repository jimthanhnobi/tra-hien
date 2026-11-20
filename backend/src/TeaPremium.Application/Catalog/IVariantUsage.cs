namespace TeaPremium.Application.Catalog;

// Sales supplies its order-line usage implementation alongside P2 order creation; no guessed Sales schema in P1.
public interface IVariantUsage
{
    Task<bool> UsedAsync(Guid variantId, CancellationToken ct);
}
