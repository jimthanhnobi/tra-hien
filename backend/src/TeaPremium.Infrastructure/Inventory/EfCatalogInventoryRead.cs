using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfCatalogInventoryRead(AppDbContext db) : ICatalogInventoryRead, IVariantUsage
{
    public Task<int> OnHandAsync(Guid variantId, CancellationToken ct) =>
        db.Set<StockLevel>().Where(x => x.VariantId == variantId).Select(x => x.OnHand).SingleOrDefaultAsync(ct);

    public Task<bool> HasMovementsAsync(Guid variantId, CancellationToken ct) => db.Set<StockMovement>().AnyAsync(x => x.VariantId == variantId, ct);
    public async Task<IReadOnlyDictionary<Guid, int>> AvailableAsync(Guid[] variantIds, CancellationToken ct)
        => await db.Set<StockLevel>().Where(x => variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, x => x.OnHand - x.Reserved, ct);

    // BR-SEO-21: a unique-piece variant with available == 0 is "sold" iff net commercial sales
    // -(SUM(sale + return_restock) where ref_type IN ('order','return')) > 0.
    public async Task<IReadOnlySet<Guid>> SoldUniqueVariantIdsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        if (variantIds.Count == 0) return new HashSet<Guid>();
        var ids = variantIds.Distinct().ToArray();
        var sold = await (
            from variant in db.Set<Variant>().AsNoTracking()
            join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
            from level in levels.DefaultIfEmpty()
            where ids.Contains(variant.Id)
                && variant.IsUniquePiece
                && (level == null ? 0 : level.OnHand - level.Reserved) == 0
                && -(db.Set<StockMovement>()
                    .Where(m => m.VariantId == variant.Id
                        && (m.MovementType == "sale" || m.MovementType == "return_restock")
                        && (m.RefType == "order" || m.RefType == "return"))
                    .Sum(m => (long?)m.Quantity) ?? 0L) > 0L
            select variant.Id).ToArrayAsync(ct);
        return sold.ToHashSet();
    }

    public Task<bool> UsedAsync(Guid variantId, CancellationToken ct) => HasMovementsAsync(variantId, ct);
}
