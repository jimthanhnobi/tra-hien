using System.Data;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfInventoryQueries(AppDbContext db, IClock clock) : IInventoryQueries
{
    public async Task<CatalogPage<InventoryStockView>> ListAsync(ListInventory query, CancellationToken ct)
    {
        var rows = from variant in db.Set<Variant>()
                   join product in db.Set<Product>() on variant.ProductId equals product.Id
                   join level in db.Set<StockLevel>() on variant.Id equals level.VariantId into levels
                   from level in levels.DefaultIfEmpty()
                   let available = level == null ? 0 : level.OnHand - level.Reserved
                   let low = variant.LowStockThreshold > 0 && available <= variant.LowStockThreshold
                   select new { Variant = variant, Product = product, Level = level, Available = available, Low = low };
        if (query.Type is { } type) rows = rows.Where(x => x.Product.ProductTypeId == type);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var normalized = await db.Database.SqlQuery<string>($"SELECT public.f_unaccent({query.Q}) AS \"Value\"").SingleAsync(ct);
            foreach (var token in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var skuToken = token.ToUpperInvariant();
                rows = rows.Where(x => x.Product.SearchText.Contains(token) || x.Variant.Sku.Contains(skuToken));
            }
        }
        rows = query.Status switch
        {
            "in" => rows.Where(x => x.Available > 0),
            "out" => rows.Where(x => x.Available == 0),
            "low" => rows.Where(x => x.Low),
            _ => rows
        };
        var total = await rows.LongCountAsync(ct);
        var detailed = query.Detailed;
        var costs = detailed && query.Costs;
        var items = await rows.OrderBy(x => x.Variant.Sku).ThenBy(x => x.Variant.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new InventoryStockView(x.Variant.Id, x.Variant.ProductId, x.Product.ProductTypeId, x.Product.Name,
                x.Variant.Name, x.Variant.Sku, x.Available > 0 ? "in_stock" : "out_of_stock",
                detailed ? x.Level == null ? 0 : x.Level.OnHand : null,
                detailed ? x.Level == null ? 0 : x.Level.Reserved : null,
                detailed ? x.Available : null, detailed ? x.Low : null,
                costs && x.Level != null ? x.Level.AvgCostVnd : null)).ToArrayAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }

    public async Task<CatalogPage<InventoryMovementView>?> MovementsAsync(ListMovements query, CancellationToken ct)
    {
        if (!await db.Set<Variant>().AnyAsync(x => x.Id == query.VariantId, ct)) return null;
        var rows = db.Set<StockMovement>().Where(x => x.VariantId == query.VariantId);
        if (query.Type is { } type) rows = rows.Where(x => x.MovementType == type);
        if (query.From is { } from) rows = rows.Where(x => x.OccurredAt >= from);
        if (query.To is { } to) rows = rows.Where(x => x.OccurredAt < to);
        var total = await rows.LongCountAsync(ct);
        var costs = query.Costs;
        var items = await rows.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new InventoryMovementView(x.Id, x.MovementType, x.Quantity, x.OnHandAfter, x.RefType, x.RefId,
                x.Reason, x.ActorType, x.ActorId, x.OccurredAt, x.CreatedAt, costs ? x.UnitCostVnd : null)).ToArrayAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }

    public async Task<InventoryAlertsView> AlertsAsync(GetInventoryAlerts query, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;

        var checkedAt = clock.UtcNow.UtcDateTime;
        var deadCount = await db.OutboxMessages.AsNoTracking()
            .LongCountAsync(x => x.Type == InventoryAlert.Message && x.Status == "dead", ct);

        var lowQuery = from variant in db.Set<Variant>().AsNoTracking()
                       join product in db.Set<Product>().AsNoTracking() on variant.ProductId equals product.Id
                       join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
                       from level in levels.DefaultIfEmpty()
                       let onHand = level == null ? 0 : level.OnHand
                       let reserved = level == null ? 0 : level.Reserved
                       let available = onHand - reserved
                       where variant.LowStockThreshold > 0 && available <= variant.LowStockThreshold
                       select new { variant, product, onHand, reserved, available };

        var lowTotal = await lowQuery.LongCountAsync(ct);
        var lowItems = await lowQuery.OrderBy(x => x.variant.Sku).ThenBy(x => x.variant.Id)
            .Take(query.Limit)
            .Select(x => new LowStockAlertItem(
                x.variant.Id,
                x.variant.ProductId,
                x.product.Name,
                x.variant.Name,
                x.variant.Sku,
                x.onHand,
                x.reserved,
                x.available,
                x.variant.LowStockThreshold))
            .ToArrayAsync(ct);

        var mismatchQuery = from variant in db.Set<Variant>().AsNoTracking()
                            join product in db.Set<Product>().AsNoTracking() on variant.ProductId equals product.Id
                            join level in db.Set<StockLevel>().AsNoTracking() on variant.Id equals level.VariantId into levels
                            from level in levels.DefaultIfEmpty()
                            let hasLevel = level != null
                            let onHand = level == null ? 0 : level.OnHand
                            let ledgerQty = db.Set<StockMovement>().Where(m => m.VariantId == variant.Id).Sum(m => (long?)m.Quantity) ?? 0L
                            where ledgerQty != (long)onHand
                            select new { variant, product, hasLevel, onHand, ledgerQty };

        var mismatchTotal = await mismatchQuery.LongCountAsync(ct);
        var mismatches = await mismatchQuery.OrderBy(x => x.variant.Sku).ThenBy(x => x.variant.Id)
            .Take(query.Limit)
            .Select(x => new InventoryMismatchItem(
                x.variant.Id,
                x.variant.ProductId,
                x.product.Name,
                x.variant.Name,
                x.variant.Sku,
                x.hasLevel,
                x.onHand,
                x.ledgerQty,
                x.ledgerQty - x.onHand))
            .ToArrayAsync(ct);

        if (tx is not null)
            await tx.CommitAsync(ct);

        return new(checkedAt, deadCount, lowTotal, lowItems, mismatchTotal, mismatches);
    }
}
