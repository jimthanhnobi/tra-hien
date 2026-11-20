using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfCatalogInventoryRead(AppDbContext db) : ICatalogInventoryRead, IVariantUsage
{
    public Task<bool> HasMovementsAsync(Guid variantId, CancellationToken ct) => db.Set<StockMovement>().AnyAsync(x => x.VariantId == variantId, ct);
    public async Task<IReadOnlyDictionary<Guid, int>> AvailableAsync(Guid[] variantIds, CancellationToken ct)
        => await db.Set<StockLevel>().Where(x => variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, x => x.OnHand - x.Reserved, ct);
    public Task<bool> UsedAsync(Guid variantId, CancellationToken ct) => HasMovementsAsync(variantId, ct);
}
