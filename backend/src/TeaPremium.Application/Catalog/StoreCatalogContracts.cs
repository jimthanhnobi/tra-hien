namespace TeaPremium.Application.Catalog;

public sealed record StoreAttributeFilter(string Code, string? Option, decimal? Min, decimal? Max);
public sealed record StoreProductQuery(string? Category, string? Collection, string? Type, string? Material,
    string? Artisan, long? PriceMin, long? PriceMax, bool InStock, string? Q, string Sort,
    int Page, int PageSize, StoreAttributeFilter[] Attributes);

public sealed record StoreReference(string Slug, string Name);
public sealed record StoreImage(string Url, string? Alt, int? Width, int? Height);
public sealed record StorePrice(long EffectivePriceVnd, long? OriginalPriceVnd);
public sealed record StoreProductCard(string Slug, string Name, string? Subtitle, string? ShortDescription,
    Guid RepresentativeVariantId, StorePrice Price, string Availability, bool LowStock, StoreImage? Cover);
public sealed record StoreFacet(string Key, string Value, string Label, long ProductCount, string? Type = null);
public sealed record StoreProductPage(StoreProductCard[] Items, int Page, int PageSize, long TotalItems, StoreFacet[] Facets);
public sealed record StoreAttributeValue(string Code, string Name, string? Unit, string? Text, decimal? Number,
    string? OptionCode, string? OptionLabel, bool? Boolean, StoreDimensions? Dimensions);
public sealed record StoreDimensions(decimal? D, decimal? H, decimal? Mouth, decimal? L, decimal? W);
public sealed record StoreVariant(Guid Id, string Sku, string? Name, StorePrice Price, string Availability,
    bool LowStock, StoreAttributeValue[] Attributes);
public sealed record StoreProductImage(StoreImage Image, Guid? VariantId, bool IsCover);
public sealed record StoreSeo(string? Title, string? Description, bool Indexable);
public sealed record StoreProductSeo(string? Title, string? Description, bool Indexable, bool ManualNoindex,
    bool ThinDescription, int DescriptionWordCount, StoreImage? OgImage);
public sealed record StoreProductDetail(string Slug, string Name, string? Subtitle, string? ShortDescription,
    string? DescriptionHtml, string? CareHtml, string? OriginNote, StoreReference Type, StoreReference? Material,
    StoreReference? Artisan, StoreReference[] Categories, StoreVariant[] Variants, StoreAttributeValue[] Attributes,
    StoreProductImage[] Images, StoreProductSeo Seo, StoreReference[] RelatedProducts);
public sealed record StoreTaxonomySummary(string Slug, string Name, string? Summary, StoreImage? Image);
public sealed record StoreTaxonomyDetail(string Slug, string Name, string? Summary, string? DescriptionHtml,
    string? BodyHtml, string? Kind, StoreImage? Image, StoreSeo Seo);
public sealed record StoreCategoryNode(string Slug, string Name, StoreCategoryNode[] Children);
public sealed record StoreNavigation(StoreCategoryNode[] Categories, StoreReference[] Collections);

public enum StoreTaxonomy { Category, Collection, Material, Artisan }

public interface IStoreCatalogRead
{
    Task<StoreProductPage?> ProductsAsync(StoreProductQuery query, CancellationToken ct);
    Task<StoreProductDetail?> ProductAsync(string slug, CancellationToken ct);
    Task<StoreTaxonomySummary[]> TaxonomyListAsync(StoreTaxonomy kind, CancellationToken ct);
    Task<StoreTaxonomyDetail?> TaxonomyAsync(StoreTaxonomy kind, string slug, CancellationToken ct);
    Task<StoreNavigation> NavigationAsync(CancellationToken ct);
}
