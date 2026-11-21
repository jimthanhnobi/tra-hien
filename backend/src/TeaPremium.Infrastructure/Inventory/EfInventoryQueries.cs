using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfInventoryQueries(AppDbContext db) : IInventoryQueries
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
}
