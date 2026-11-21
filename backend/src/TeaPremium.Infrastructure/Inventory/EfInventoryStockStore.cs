using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Inventory;
using TeaPremium.Domain.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfInventoryStockStore(AppDbContext db) : IInventoryStockStore
{
    private readonly Dictionary<Guid, (InventoryPosition Position, StockLevel? Level)> locked = [];
    private Guid? lockTransactionId;

    public async Task<IReadOnlyDictionary<string, InventoryPosition>> PreviewAsync(string[] skus, CancellationToken ct)
    {
        var rows = await (from variant in db.Set<Variant>()
                          join product in db.Set<Product>() on variant.ProductId equals product.Id
                          join level in db.Set<StockLevel>() on variant.Id equals level.VariantId into levels
                          from level in levels.DefaultIfEmpty()
                          where skus.Contains(variant.Sku)
                          select new InventoryPosition(new(variant.Id, variant.ProductId, variant.Sku, product.Slug, variant.IsUniquePiece),
                              new(level == null ? 0 : level.OnHand, level == null ? 0 : level.Reserved, level == null ? null : level.AvgCostVnd),
                              db.Set<StockMovement>().Any(x => x.VariantId == variant.Id))).ToArrayAsync(ct);
        return rows.ToDictionary(x => x.Variant.Sku, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<Guid, InventoryPosition>> LockAsync(Guid[] variantIds, CancellationToken ct)
    {
        lockTransactionId = CurrentTransactionId();
        locked.Clear();
        var parentIds = await db.Set<Variant>().Where(x => variantIds.Contains(x.Id))
            .Select(x => x.ProductId).Distinct().ToArrayAsync(ct);
        var products = await db.Set<Product>().FromSqlInterpolated(
            $"SELECT *, xmin FROM catalog.products WHERE id=ANY({parentIds}) ORDER BY id FOR NO KEY UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var variants = await db.Set<Variant>().FromSqlInterpolated(
            $"SELECT *, xmin FROM catalog.variants WHERE id=ANY({variantIds}) AND product_id=ANY({productIds}) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);

        var ids = variants.Select(x => x.Id).ToArray();
        var levels = (await db.Set<StockLevel>().FromSqlInterpolated(
            $"SELECT * FROM inventory.stock_levels WHERE variant_id=ANY({ids}) ORDER BY variant_id FOR UPDATE")
            .ToArrayAsync(ct)).ToDictionary(x => x.VariantId);
        var moved = (await db.Set<StockMovement>().Where(x => ids.Contains(x.VariantId))
            .Select(x => x.VariantId).Distinct().ToArrayAsync(ct)).ToHashSet();
        var slugs = products.ToDictionary(x => x.Id, x => x.Slug);
        foreach (var variant in variants)
        {
            var level = levels.GetValueOrDefault(variant.Id);
            var balance = level is null ? default : new StockBalance(level.OnHand, level.Reserved, level.AvgCostVnd);
            var position = new InventoryPosition(new(variant.Id, variant.ProductId, variant.Sku, slugs[variant.ProductId],
                variant.IsUniquePiece), balance, moved.Contains(variant.Id));
            locked.Add(variant.Id, (position, level));
        }
        return locked.ToDictionary(x => x.Key, x => x.Value.Position);
    }

    public void Stage(StockPosting posting, DateTime now)
    {
        if (CurrentTransactionId() != lockTransactionId)
            throw new InvalidOperationException("Stock posting requires locks acquired in this transaction.");
        var id = posting.Position.Variant.Id;
        if (!locked.Remove(id, out var state) || !ReferenceEquals(state.Position, posting.Position))
            throw new InvalidOperationException("Stock posting requires current locked state and one posting per variant.");

        var level = state.Level;
        if (level is null)
        {
            level = new StockLevel { VariantId = id };
            db.Add(level);
        }
        level.OnHand = posting.BalanceAfter.OnHand;
        level.AvgCostVnd = posting.BalanceAfter.AvgCostVnd;
        level.UpdatedAt = now;
        var change = posting.Change;
        db.Add(new StockMovement
        {
            Id = Guid.CreateVersion7(),
            VariantId = id,
            MovementType = change.Type,
            Quantity = change.Quantity,
            UnitCostVnd = change.UnitCostVnd,
            OnHandAfter = level.OnHand,
            RefType = change.RefType,
            RefId = change.RefId,
            Reason = change.Reason,
            ActorType = change.ActorType,
            ActorId = change.ActorId,
            OccurredAt = now,
            CreatedAt = now
        });
    }

    private Guid CurrentTransactionId() => db.Database.CurrentTransaction?.TransactionId
        ?? throw new InvalidOperationException("Inventory writes require the command transaction.");
}
