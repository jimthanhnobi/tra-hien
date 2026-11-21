using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.Results;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Pricing;

public sealed class EfSalePriceStore(AppDbContext db, IOutboxWriter outbox) : ISalePriceStore
{
    public async Task<SalePriceList?> ListAsync(Guid variantId, CancellationToken ct)
    {
        var snapshot = await db.Set<Variant>().AsNoTracking().Where(x => x.Id == variantId)
            .Select(variant => new
            {
                variant.Version,
                Prices = db.Set<SalePrice>().Where(x => x.VariantId == variant.Id).OrderBy(x => x.StartsAt).ThenBy(x => x.Id).ToArray()
            }).AsSingleQuery().SingleOrDefaultAsync(ct);
        return snapshot is null ? null : new(CatalogVersions.Encode(snapshot.Version), snapshot.Prices.Select(View).ToArray());
    }

    public async Task<SalePriceVariant?> LockVariantAsync(Guid variantId, CancellationToken ct)
    {
        // Discover only the parent key. Read price/state after taking the same parent lock as P1-05.
        var productId = await db.Set<Variant>().Where(x => x.Id == variantId).Select(x => (Guid?)x.ProductId).SingleOrDefaultAsync(ct);
        if (productId is not { } id || await EfProductStore.LockProductAsync(db, id, ct) is not { } product) return null;

        // Pricing touches no mutable taxonomy/redirect state. Keep the product -> variant order.
        var rows = await db.Set<Variant>().FromSqlInterpolated(
            $"SELECT *, xmin FROM catalog.variants WHERE product_id={id} AND id={variantId} FOR UPDATE").ToArrayAsync(ct);
        var variant = rows.SingleOrDefault();
        return variant is null ? null : new(id, product.Slug, variant.Version, variant.Status == "active", variant.PriceVnd);
    }

    public async Task<Result<SalePriceView>> AddAsync(SalePriceVariant variant, CreateSalePrice command, DateTime now, CancellationToken ct)
    {
        var promotion = new SalePrice
        {
            Id = Guid.CreateVersion7(),
            VariantId = command.VariantId,
            SalePriceVnd = command.SalePriceVnd,
            StartsAt = command.StartsAt,
            EndsAt = command.EndsAt,
            Note = command.Note,
            CreatedBy = command.Actor.Id,
            CreatedAt = now
        };
        db.Add(promotion);
        await EnqueueAsync(variant, ct);
        try
        {
            // Flush inside UnitOfWork so the immediate GiST exclusion violation maps to a business error.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.ExclusionViolation, ConstraintName: "ex_sale_prices_no_overlap", SchemaName: "pricing", TableName: "sale_prices" })
        {
            return Result.Failure<SalePriceView>(new("sale_price_overlap", "Khoảng thời gian khuyến mãi bị chồng lấn.", ErrorType.Conflict));
        }
        return Result.Success(View(promotion));
    }

    public async Task<Result<bool>> DeleteAsync(SalePriceVariant variant, DeleteSalePrice command, SalePriceVersion version, CancellationToken ct)
    {
        var rows = await db.Set<SalePrice>().FromSqlInterpolated(
            $"SELECT *, xmin FROM pricing.sale_prices WHERE variant_id={command.VariantId} AND id={command.SalePriceId} FOR UPDATE").ToArrayAsync(ct);
        var promotion = rows.SingleOrDefault();
        if (promotion is null) return Result.Failure<bool>(SalePriceErrors.NotFound);
        if (new SalePriceVersion(promotion.Id, promotion.Version) != version) return Result.Failure<bool>(SalePriceErrors.Stale);

        db.Remove(promotion);
        await EnqueueAsync(variant, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Result.Failure<bool>(SalePriceErrors.Stale); }
        return Result.Success(true);
    }

    private async Task EnqueueAsync(SalePriceVariant variant, CancellationToken ct)
    {
        var slugs = await (from link in db.Set<ProductCategory>().Where(x => x.ProductId == variant.ProductId)
                           join category in db.Set<Category>() on link.CategoryId equals category.Id
                           select category.Slug).ToArrayAsync(ct);
        ProductInvalidation.Enqueue(outbox, variant.ProductSlug, slugs);
    }

    private static SalePriceView View(SalePrice row) => new(row.Id, row.SalePriceVnd, row.StartsAt, row.EndsAt, row.Note,
        row.CreatedAt, new SalePriceVersion(row.Id, row.Version).Encode());
}
