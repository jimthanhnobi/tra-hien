using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfProductStore(AppDbContext db, IConfiguration configuration, ICatalogStore routing,
    IOutboxWriter outbox, ProductAudit audit, TeaPremium.Application.Inventory.ICatalogInventoryRead inventory, IEnumerable<IVariantUsage> usage,
    TeaPremium.Application.Media.IPublicImageAssets assets) : IProductStore
{
    public async Task LockAsync(bool routingWrite, CancellationToken ct)
    {
        // P1-03 reference contract: BEFORE all reads, rows, value writes and redirect graph.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE catalog.product_types, catalog.attribute_definitions, catalog.attribute_options, catalog.materials IN SHARE MODE", ct);
        if (routingWrite) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8104,4)", ct);
    }
    public async Task<ProductReference?> ReferenceAsync(Guid type, Guid? material, CancellationToken ct)
    {
        var t = await db.Set<ProductType>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == type, ct);
        if (t is null) return null;
        var attributes = await db.Set<AttributeDefinition>().AsNoTracking().Where(x => x.ProductTypeId == type).ToArrayAsync(ct);
        var ids = attributes.Select(x => x.Id).ToArray();
        var options = await db.Set<AttributeOption>().AsNoTracking().Where(x => ids.Contains(x.AttributeId)).Select(x => new TypedAttributeOption(x.Id, x.AttributeId, x.IsActive)).ToArrayAsync(ct);
        var code = material is null ? null : await db.Set<Material>().Where(x => x.Id == material).Select(x => x.SkuCode).SingleOrDefaultAsync(ct);
        return new(type, t.SkuPrefix, t.IsActive, code, attributes.Select(EfCatalogReference.View).ToArray(), options);
    }
    public async Task<string> AllocateBaseAsync(ProductReference reference, bool hasMaterial, CancellationToken ct)
    {
        var number = await db.Database.SqlQueryRaw<long>("SELECT nextval('catalog.sku_number_seq') AS \"Value\"").SingleAsync(ct);
        return reference.Prefix + "-" + (hasMaterial ? reference.MaterialCode : "XX") + "-" + number.ToString("D4", CultureInfo.InvariantCulture);
    }
    public Task<bool> SlugExistsAsync(string slug, Guid id, CancellationToken ct) => db.Set<Product>().AnyAsync(x => x.Slug == slug && x.Id != id, ct);
    public async Task<bool> ReferencesExistAsync(ProductData p, CancellationToken ct)
    {
        // Lock mutable taxonomy state before graph access; never take a material row lock after SHARE.
        var categoryIds = p.CategoryIds.Concat(p.PrimaryCategoryId is { } primary ? [primary] : Array.Empty<Guid>()).Distinct().ToArray();
        var categories = await db.Set<Category>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.categories WHERE id=ANY({categoryIds}) ORDER BY id FOR SHARE").ToArrayAsync(ct);
        if (categories.Length != categoryIds.Length) return false;
        if (p.MaterialId is { } material && !await db.Set<Material>().AnyAsync(x => x.Id == material, ct)) return false;
        if (p.ArtisanId is { } artisan && (await db.Set<Artisan>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.artisans WHERE id={artisan} FOR SHARE").ToArrayAsync(ct)).Length != 1) return false;
        return await db.Set<Product>().CountAsync(x => p.RelatedProductIds.Contains(x.Id), ct) == p.RelatedProductIds.Count;
    }
    public Task<bool> CategoryVisibleAsync(Guid id, CancellationToken ct) => db.Set<Category>().AnyAsync(x => x.Id == id && x.Status == "visible", ct);
    public Task<bool> PublicImageAsync(Guid asset, CancellationToken ct) => assets.ExistsAsync(asset, ct);
    public async Task<bool> VariantUsedAsync(Guid id, CancellationToken ct)
    {
        foreach (var source in usage) if (await source.UsedAsync(id, ct)) return true;
        return false;
    }
    public async Task<string?> ArchiveTargetAsync(ProductData p, CancellationToken ct)
    {
        if (p.ArtisanId is { } artisan)
        { var slug = await db.Set<Artisan>().Where(x => x.Id == artisan && x.Status == "published" && x.FirstPublishedAt != null).Select(x => x.Slug).SingleOrDefaultAsync(ct); if (slug is not null) return "/nghe-nhan/" + slug; }
        if (p.MaterialId is { } material)
        { var slug = await db.Set<Material>().Where(x => x.Id == material && x.Status == "published" && x.FirstPublishedAt != null).Select(x => x.Slug).SingleOrDefaultAsync(ct); if (slug is not null) return "/chat-lieu/" + slug; }
        if (p.PrimaryCategoryId is { } category)
        { var slug = await db.Set<Category>().Where(x => x.Id == category && x.Status == "visible" && x.FirstPublishedAt != null).Select(x => x.Slug).SingleOrDefaultAsync(ct); if (slug is not null) return "/danh-muc/" + slug; }
        return null;
    }
    public async Task<ProductData?> LoadAsync(Guid id, bool update, CancellationToken ct)
    {
        // Parent keys never change. NO KEY UPDATE serializes writers while allowing FK key-share
        // reads from related products, avoiding A->B / B->A relation-insert deadlocks.
        var row = update ? (await db.Set<Product>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.products WHERE id={id} FOR NO KEY UPDATE").ToArrayAsync(ct)).SingleOrDefault()
            : await db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return null;
        var p = ToData(row);
        var variants = update ? await db.Set<Variant>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.variants WHERE product_id={id} ORDER BY id FOR UPDATE").ToArrayAsync(ct)
            : await db.Set<Variant>().AsNoTracking().Where(x => x.ProductId == id).ToArrayAsync(ct);
        var variantIds = variants.Select(x => x.Id).ToArray();
        var values = await db.Set<VariantAttributeValue>().AsNoTracking().Where(x => variantIds.Contains(x.VariantId)).ToArrayAsync(ct);
        p.Variants = variants.OrderBy(x => x.Position).ThenBy(x => x.Id).Select(x => new VariantData
        {
            Id = x.Id,
            Sku = x.Sku,
            Name = x.Name,
            Position = x.Position,
            PriceVnd = x.PriceVnd,
            IsUniquePiece = x.IsUniquePiece,
            WeightG = x.WeightG,
            PackageDimsCm = x.PackageDimsCm,
            Barcode = x.Barcode,
            LowStockThreshold = x.LowStockThreshold,
            Status = x.Status,
            Version = x.Version,
            Attributes = values.Where(v => v.VariantId == x.Id).Select(v => Value(v.AttributeId, v.ValueText, v.ValueNumber, v.OptionId, v.ValueBool, v.ValueJson)).ToList()
        }).ToList();
        p.Attributes = (await db.Set<ProductAttributeValue>().AsNoTracking().Where(x => x.ProductId == id).ToArrayAsync(ct)).Select(v => Value(v.AttributeId, v.ValueText, v.ValueNumber, v.OptionId, v.ValueBool, v.ValueJson)).ToList();
        p.CategoryIds = await db.Set<ProductCategory>().Where(x => x.ProductId == id).OrderBy(x => x.CategoryId).Select(x => x.CategoryId).ToListAsync(ct);
        p.RelatedProductIds = await db.Set<ProductRelation>().Where(x => x.ProductId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.RelatedProductId).Select(x => x.RelatedProductId).ToListAsync(ct);
        p.Media = await db.Set<ProductMedia>().AsNoTracking().Where(x => x.ProductId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ProductImage { Id = x.Id, AssetId = x.AssetId, VariantId = x.VariantId, AltText = x.AltText, SortOrder = x.SortOrder, IsCover = x.IsCover }).ToListAsync(ct);
        foreach (var image in p.Media) image.Url = await assets.UrlAsync(image.AssetId, ct);
        p.Indexable = p.Status == "active" && !p.RobotsNoindex && !p.ThinDescription && string.Equals(configuration["SITE_INDEXABLE"], "true", StringComparison.OrdinalIgnoreCase);
        if (p.ThinDescription) p.Warnings.Add(new("thin_description_noindex", "descriptionHtml", p.DescriptionWordCount, 150));
        return p;
    }
    public async Task<CatalogPage<ProductData>> ListAsync(ListProducts query, CancellationToken ct)
    {
        var rows = db.Set<Product>().AsNoTracking();
        if (query.Q is { Length: > 0 } q)
        {
            var normalized = await db.Database.SqlQuery<string>($"SELECT public.f_unaccent({q}) AS \"Value\"").SingleAsync(ct);
            // Each captured token becomes a parameterized literal Contains predicate (AND).
            // Split(null) uses Unicode whitespace; Npgsql escapes LIKE metacharacters for Contains.
            foreach (var token in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
                rows = rows.Where(x => x.SearchText.Contains(token));
        }
        if (query.Status is not null) rows = rows.Where(x => x.Status == query.Status);
        if (query.Type is { } type) rows = rows.Where(x => x.ProductTypeId == type);
        if (query.Category is { } category) rows = rows.Where(x => db.Set<ProductCategory>().Any(c => c.ProductId == x.Id && c.CategoryId == category));
        if (query.Material is { } material) rows = rows.Where(x => x.MaterialId == material);
        if (query.Artisan is { } artisan) rows = rows.Where(x => x.ArtisanId == artisan);
        if (query.Stock is not null)
        {
            var variants = await db.Set<Variant>().Select(v => new { v.Id, v.ProductId, v.LowStockThreshold }).ToArrayAsync(ct);
            var available = await inventory.AvailableAsync(variants.Select(x => x.Id).ToArray(), ct);
            var matching = variants.Where(v => query.Stock == "out" ? available.GetValueOrDefault(v.Id) <= 0
                : available.GetValueOrDefault(v.Id) > 0 && (query.Stock != "low" || available.GetValueOrDefault(v.Id) <= v.LowStockThreshold)).Select(x => x.ProductId).Distinct().ToArray();
            rows = rows.Where(x => matching.Contains(x.Id));
        }
        var total = await rows.LongCountAsync(ct);
        var ids = await rows.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(x => x.Id).ToArrayAsync(ct);
        var result = new List<ProductData>(); foreach (var id in ids) result.Add((await LoadAsync(id, false, ct))!);
        return new(result, query.Page, query.PageSize, total);
    }
    private static ProductValue Value(Guid id, string? text, decimal? number, Guid? option, bool? boolean, string? json)
        => new(id, text, number, option, boolean, json is null ? null : JsonSerializer.Deserialize<JsonElement>(json));
    private static ProductData ToData(Product x) => new()
    {
        Id = x.Id,
        SkuBase = x.SkuBase,
        ProductTypeId = x.ProductTypeId,
        Slug = x.Slug,
        Name = x.Name,
        Subtitle = x.Subtitle,
        ShortDescription = x.ShortDescription,
        DescriptionJson = x.DescriptionJson,
        DescriptionHtml = x.DescriptionHtml,
        CareHtml = x.CareHtml,
        DescriptionWordCount = x.DescriptionWordCount,
        PrimaryCategoryId = x.PrimaryCategoryId,
        MaterialId = x.MaterialId,
        ArtisanId = x.ArtisanId,
        OriginNote = x.OriginNote,
        Status = x.Status,
        PublishedAt = x.PublishedAt,
        FirstPublishedAt = x.FirstPublishedAt,
        ArchivedRedirectPath = x.ArchivedRedirectPath,
        SoldOutAt = x.SoldOutAt,
        SeoKeepAfterSold = x.SeoKeepAfterSold,
        IsFeatured = x.IsFeatured,
        FocusKeyword = x.FocusKeyword,
        SeoTitle = x.SeoTitle,
        SeoDescription = x.SeoDescription,
        CanonicalUrl = x.CanonicalUrl,
        RobotsNoindex = x.RobotsNoindex,
        OgMediaId = x.OgMediaId,
        SearchText = x.SearchText,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        Version = x.Version
    };
}
