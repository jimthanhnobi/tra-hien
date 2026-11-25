using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

// BR-SEO-21: maintain products.sold_out_at and emit StorefrontInvalidate whenever unique-piece sold state changes.
public sealed class EfProductSoldOutSync(
    AppDbContext db,
    ICatalogInventoryRead inventory,
    IOutboxWriter outbox,
    IClock clock) : IProductSoldOutSync
{
    public async Task SyncSoldOutStateAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Sold-out synchronization requires an active transaction.");

        var orderedIds = productIds.Distinct().Order().ToArray();
        var products = await db.Set<Product>()
            .FromSqlInterpolated($"SELECT *, xmin FROM catalog.products WHERE id = ANY({orderedIds}) ORDER BY id FOR NO KEY UPDATE")
            .ToArrayAsync(ct);
        if (products.Length == 0) return;

        var activeVariants = await db.Set<Variant>().AsNoTracking()
            .Where(v => orderedIds.Contains(v.ProductId) && v.Status == "active")
            .Select(v => new { v.Id, v.ProductId, v.IsUniquePiece })
            .ToArrayAsync(ct);

        var uniqueActiveIds = activeVariants.Where(v => v.IsUniquePiece).Select(v => v.Id).ToArray();
        IReadOnlySet<Guid> soldUniqueIds = uniqueActiveIds.Length == 0
            ? new HashSet<Guid>()
            : await inventory.SoldUniqueVariantIdsAsync(uniqueActiveIds, ct);

        var categorySlugsByProduct = (await (
            from link in db.Set<ProductCategory>().AsNoTracking()
            join cat in db.Set<Category>().AsNoTracking() on link.CategoryId equals cat.Id
            where orderedIds.Contains(link.ProductId) && cat.Status == "visible"
            select new { link.ProductId, cat.Slug }).ToArrayAsync(ct))
            .ToLookup(x => x.ProductId, x => x.Slug);

        var variantsByProduct = activeVariants.ToLookup(v => v.ProductId);
        var now = clock.UtcNow.UtcDateTime;
        var invalidationTags = new HashSet<string>(StringComparer.Ordinal);
        var anyProductUpdated = false;

        foreach (var product in products)
        {
            var actives = variantsByProduct[product.Id].ToArray();
            // R-P205-03: require activeVariants.Length > 0 to avoid vacuous truth.
            var allUniqueAndSold = actives.Length > 0
                && actives.All(v => v.IsUniquePiece && soldUniqueIds.Contains(v.Id));

            var soldOutChanged = false;
            if (allUniqueAndSold && product.SoldOutAt is null)
            {
                product.SoldOutAt = now;
                product.UpdatedAt = now;
                soldOutChanged = true;
                anyProductUpdated = true;
            }
            else if (!allUniqueAndSold && product.SoldOutAt is not null)
            {
                product.SoldOutAt = null;
                product.UpdatedAt = now;
                soldOutChanged = true;
                anyProductUpdated = true;
            }

            // R-P205-02: transitioning confirmed -> shipping keeps available == 0, so explicitly
            // invalidate storefront whenever sold_out_at changed or any active unique piece belongs to the product.
            if (soldOutChanged || actives.Any(v => v.IsUniquePiece))
            {
                invalidationTags.Add("product:" + product.Slug);
                foreach (var slug in categorySlugsByProduct[product.Id])
                    invalidationTags.Add("category:" + slug);
            }
        }

        if (anyProductUpdated)
            await db.SaveChangesAsync(ct);

        if (invalidationTags.Count > 0)
        {
            var fixedTags = new[] { "catalog", "sitemap", "feed" };
            var dynamicTags = invalidationTags.Order(StringComparer.Ordinal).ToArray();
            foreach (var batch in dynamicTags.Chunk(200 - fixedTags.Length))
            {
                outbox.Enqueue(
                    StorefrontInvalidate.Message,
                    JsonSerializer.Serialize(new StorefrontInvalidate([.. batch, .. fixedTags])));
            }
        }
    }
}
