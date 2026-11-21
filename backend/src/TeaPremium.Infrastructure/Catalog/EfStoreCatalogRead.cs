using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Pricing;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfStoreCatalogRead(AppDbContext db, IClock clock,
    IConfiguration config, ICatalogInventoryRead inventory, ICatalogStore catalog,
    ICatalogContent content, MediaStorageOptions media) : IStoreCatalogRead
{
    private bool SiteIndexable => string.Equals(config["SITE_INDEXABLE"], "true", StringComparison.OrdinalIgnoreCase);
    private static StorePrice Offer(long original, long effective) => new(effective, effective < original ? original : null);
    // BR-SEO-21: no authoritative sale producer exists at this phase.
    private static string Availability(int available) => available > 0 ? "in_stock" : "out_of_stock";
    private static bool LowStock(int available) => available is > 0 and <= 3;

    private IQueryable<Variant> EligibleVariants => db.Set<Variant>().AsNoTracking()
        .Where(v => v.Status == "active" && v.PriceVnd >= 1 && v.PriceVnd <= 10_000_000_000L);

    // BR-CAT-09: the supported hierarchy is at most three levels, from a visible root.
    private IQueryable<Category> VisibleCategories
    {
        get
        {
            var visible = db.Set<Category>().AsNoTracking().Where(c => c.Status == "visible");
            var roots = visible.Where(c => c.ParentId == null).Select(c => c.Id);
            var children = visible.Where(c => c.ParentId != null && roots.Contains(c.ParentId.Value)).Select(c => c.Id);
            return visible.Where(c => c.ParentId == null || roots.Contains(c.ParentId.Value) || children.Contains(c.ParentId.Value));
        }
    }

    private async Task<Dictionary<Guid, StoreImage>> ImagesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var keys = ids.OfType<Guid>().Distinct().ToArray();
        if (keys.Length == 0) return [];
        var assets = await EfPublicImageAssets.Query(db).Where(a => keys.Contains(a.Id) && !a.StorageKey.StartsWith("pending/"))
            .Select(a => new { a.Id, a.StorageKey, a.DefaultAlt, a.Width, a.Height }).ToArrayAsync(ct);
        var result = new Dictionary<Guid, StoreImage>();
        foreach (var asset in assets)
        {
            var url = EfPublicImageAssets.PublicUrl(media, asset.StorageKey);
            if (url is not null) result.Add(asset.Id, new(url, asset.DefaultAlt, asset.Width, asset.Height));
        }
        return result;
    }

    private static StoreImage? Image(Dictionary<Guid, StoreImage> images, Guid? id, string? alt = null)
        => id is { } key && images.TryGetValue(key, out var image) ? image with { Alt = alt ?? image.Alt } : null;

    public async Task<StoreProductDetail?> ProductAsync(string slug, CancellationToken ct)
    {
        if (slug.Contains('\0')) return null;
        var at = clock.UtcNow;
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var row = await (
            from p in db.Set<Product>().AsNoTracking()
            join t in db.Set<ProductType>() on p.ProductTypeId equals t.Id
            join m in db.Set<Material>().Where(x => x.Status == "published") on p.MaterialId equals m.Id into materials
            from m in materials.DefaultIfEmpty()
            join a in db.Set<Artisan>().Where(x => x.Status == "published") on p.ArtisanId equals a.Id into artisans
            from a in artisans.DefaultIfEmpty()
            where p.Slug == slug && p.Status == "active"
            select new
            {
                p.Id,
                p.Slug,
                p.Name,
                p.Subtitle,
                p.ShortDescription,
                p.DescriptionHtml,
                p.CareHtml,
                p.OriginNote,
                p.DescriptionWordCount,
                p.RobotsNoindex,
                p.SeoTitle,
                p.SeoDescription,
                p.OgMediaId,
                Type = new StoreReference(t.Code, t.Name),
                Material = m == null ? null : new StoreReference(m.Slug, m.Name),
                Artisan = a == null ? null : new StoreReference(a.Slug, a.Name)
            }).SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var instant = at.UtcDateTime;
        var variants = await EligibleVariants.Where(v => v.ProductId == row.Id)
            .OrderBy(v => v.Position).ThenBy(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.Sku,
                v.Name,
                v.PriceVnd,
                Promotions = db.Set<SalePrice>().Where(s => s.VariantId == v.Id && s.StartsAt <= instant && instant < s.EndsAt)
                    .Select(s => new { s.SalePriceVnd, s.StartsAt, s.EndsAt }).ToArray()
            }).AsSingleQuery().ToArrayAsync(ct);
        var ids = variants.Select(v => v.Id).ToArray();
        IReadOnlyDictionary<Guid, int> stock = ids.Length == 0 ? new Dictionary<Guid, int>() : await inventory.AvailableAsync(ids, ct);
        var values = await DetailAttributesAsync(row.Id, ids, ct);
        var views = variants.Select(v =>
        {
            var price = PricingCalculator.Calculate(true, v.PriceVnd,
                v.Promotions.Select(s => new ScheduledPrice(s.SalePriceVnd, s.StartsAt, s.EndsAt)), at)!;
            var available = stock.GetValueOrDefault(v.Id);
            return new StoreVariant(v.Id, v.Sku, v.Name, Offer(price.BasePriceVnd, price.EffectivePriceVnd),
                Availability(available), LowStock(available), values[v.Id].ToArray());
        }).ToArray();

        var links = await db.Set<ProductMedia>().Where(x => x.ProductId == row.Id &&
                (x.VariantId == null || ids.Contains(x.VariantId.Value)))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.AssetId, x.VariantId, x.AltText, x.IsCover }).ToArrayAsync(ct);
        var assets = await ImagesAsync(links.Select(x => (Guid?)x.AssetId).Append(row.OgMediaId), ct);
        var pictures = links.Where(x => assets.ContainsKey(x.AssetId))
            .Select(x => new StoreProductImage(Image(assets, x.AssetId, x.AltText)!, x.VariantId, x.IsCover)).ToArray();
        var categories = await (from link in db.Set<ProductCategory>()
                                join c in VisibleCategories on link.CategoryId equals c.Id
                                where link.ProductId == row.Id
                                orderby c.SortOrder, c.Id
                                select new StoreReference(c.Slug, c.Name)).ToArrayAsync(ct);
        var related = await (from link in db.Set<ProductRelation>()
                             join p in db.Set<Product>() on link.RelatedProductId equals p.Id
                             where link.ProductId == row.Id && p.Status == "active" && EligibleVariants.Any(v => v.ProductId == p.Id)
                             orderby link.SortOrder, p.Id
                             select new StoreReference(p.Slug, p.Name)).ToArrayAsync(ct);

        var thin = row.DescriptionWordCount < 150;
        var seo = new StoreProductSeo(row.SeoTitle, row.SeoDescription, SiteIndexable && !thin && !row.RobotsNoindex,
            row.RobotsNoindex, thin, row.DescriptionWordCount, Image(assets, row.OgMediaId));
        return new(row.Slug, row.Name, row.Subtitle, row.ShortDescription, row.DescriptionHtml, row.CareHtml, row.OriginNote,
            row.Type, row.Material, row.Artisan, categories, views, values[row.Id].ToArray(), pictures, seo, related);
    }

    private async Task<ILookup<Guid, StoreAttributeValue>> DetailAttributesAsync(Guid product, Guid[] variants, CancellationToken ct)
    {
        var raw = db.Set<ProductAttributeValue>().Where(x => x.ProductId == product)
            .Select(x => new { Owner = x.ProductId, Scope = "product", x.AttributeId, x.ValueText, x.ValueNumber, x.OptionId, x.ValueBool, x.ValueJson })
            .Concat(db.Set<VariantAttributeValue>().Where(x => variants.Contains(x.VariantId))
                .Select(x => new { Owner = x.VariantId, Scope = "variant", x.AttributeId, x.ValueText, x.ValueNumber, x.OptionId, x.ValueBool, x.ValueJson }));
        var values = await (
            from value in raw
            join definition in db.Set<AttributeDefinition>() on value.AttributeId equals definition.Id
            join option in db.Set<AttributeOption>().Where(o => o.IsActive) on
                new { Id = value.OptionId, AttributeId = value.AttributeId } equals
                new { Id = (Guid?)option.Id, option.AttributeId } into options
            from option in options.DefaultIfEmpty()
            where definition.IsActive && definition.IsVisible && definition.Scope == value.Scope
                && (value.OptionId == null || option != null)
            orderby definition.SortOrder, definition.Id
            select new
            {
                value.Owner,
                definition.Code,
                definition.Name,
                definition.Unit,
                value.ValueText,
                value.ValueNumber,
                OptionCode = option == null ? null : option.Code,
                OptionLabel = option == null ? null : option.Label,
                value.ValueBool,
                value.ValueJson
            }).ToArrayAsync(ct);
        return values.ToLookup(v => v.Owner, v => new StoreAttributeValue(v.Code, v.Name, v.Unit, v.ValueText, v.ValueNumber,
            v.OptionCode, v.OptionLabel, v.ValueBool, Dimensions(v.ValueJson)));
    }

    private static StoreDimensions? Dimensions(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) return null;
        decimal? Read(string key) => value.TryGetProperty(key, out var number) &&
            number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out var result) ? result : null;
        return new(Read("d"), Read("h"), Read("mouth"), Read("l"), Read("w"));
    }
}
