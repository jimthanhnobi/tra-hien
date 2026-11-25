namespace TeaPremium.Application.Catalog;

// BR-SEO-21: synchronize products.sold_out_at and emit StorefrontInvalidate when unique-piece sold state changes.
public interface IProductSoldOutSync
{
    Task SyncSoldOutStateAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}
