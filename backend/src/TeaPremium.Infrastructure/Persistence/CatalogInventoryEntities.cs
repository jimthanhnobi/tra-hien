using TeaPremium.Domain;
using TeaPremium.Domain.Catalog;

// P1-01 persistence only; business behavior belongs to later roadmap tasks.
namespace TeaPremium.Infrastructure.Persistence;

public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public string StorageKey { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long ByteSize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? DefaultAlt { get; set; }
    public string Purpose { get; set; } = null!;
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UploadSession
{
    public Guid Id { get; set; }
    public string StorageKey { get; set; } = null!;
    public string OriginalName { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long ExpectedBytes { get; set; }
    public string Purpose { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? AssetId { get; set; }
}

[TeaPremium.Domain.Audited]
public sealed class ProductType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string? SkuPrefix { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? GoogleProductCategory { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[TeaPremium.Domain.Audited]
public sealed class AttributeDefinition
{
    public Guid Id { get; set; }
    public Guid ProductTypeId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string DataType { get; set; } = null!;
    public string? Unit { get; set; }
    public string Scope { get; set; } = null!;
    public bool IsVariantAxis { get; set; }
    public bool IsRequiredForPublish { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsVisible { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

[TeaPremium.Domain.Audited]
public sealed class AttributeOption
{
    public Guid Id { get; set; }
    public Guid AttributeId { get; set; }
    public string Code { get; set; } = null!;
    public string Label { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

[Audited]
public sealed class Material : ITaxonomyRecord
{
    public DateTime? FirstPublishedAt { get; set; }
    public uint Version { get; set; }
    public Guid Id { get; set; }
    public string? SkuCode { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string? Summary { get; set; }
    public string? DescriptionJson { get; set; }
    public string? DescriptionHtml { get; set; }
    public Guid? CoverMediaId { get; set; }
    public string Status { get; set; } = null!;
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public bool RobotsNoindex { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Audited]
public sealed class Artisan : ITaxonomyRecord
{
    public DateTime? FirstPublishedAt { get; set; }
    public uint Version { get; set; }
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Title { get; set; }
    public string? BioJson { get; set; }
    public string? BioHtml { get; set; }
    public Guid? PortraitMediaId { get; set; }
    public string Status { get; set; } = null!;
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public bool RobotsNoindex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Audited]
public sealed class Category : ITaxonomyRecord
{
    public DateTime? FirstPublishedAt { get; set; }
    public uint Version { get; set; }
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? DescriptionHtml { get; set; }
    public string? BodyHtml { get; set; }
    public string? BodyJson { get; set; }
    public Guid? CoverMediaId { get; set; }
    public string Status { get; set; } = null!;
    public int SortOrder { get; set; }
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public bool RobotsNoindex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Product
{
    public Guid Id { get; set; }
    public string SkuBase { get; set; } = null!;
    public Guid ProductTypeId { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Subtitle { get; set; }
    public string? ShortDescription { get; set; }
    public string? DescriptionJson { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? CareHtml { get; set; }
    public int DescriptionWordCount { get; set; }
    public Guid? PrimaryCategoryId { get; set; }
    public Guid? MaterialId { get; set; }
    public Guid? ArtisanId { get; set; }
    public string? OriginNote { get; set; }
    public string Status { get; set; } = null!;
    public DateTime? PublishedAt { get; set; }
    public DateTime? FirstPublishedAt { get; set; }
    public string? ArchivedRedirectPath { get; set; }
    public DateTime? SoldOutAt { get; set; }
    public bool SeoKeepAfterSold { get; set; }
    public string? FocusKeyword { get; set; }
    public bool IsFeatured { get; set; }
    public string SearchText { get; set; } = null!;
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public string? CanonicalUrl { get; set; }
    public bool RobotsNoindex { get; set; }
    public Guid? OgMediaId { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ProductCategory
{
    public Guid ProductId { get; set; }
    public Guid CategoryId { get; set; }
}

public sealed class ProductRelation
{
    public Guid ProductId { get; set; }
    public Guid RelatedProductId { get; set; }
    public int SortOrder { get; set; }
}

[Audited]
public sealed class Variant
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = null!;
    public string? Name { get; set; }
    public int Position { get; set; }
    public long? PriceVnd { get; set; }
    public bool IsUniquePiece { get; set; }
    public int? WeightG { get; set; }
    public string? PackageDimsCm { get; set; }
    public string? Barcode { get; set; }
    public int LowStockThreshold { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ProductAttributeValue
{
    public Guid ProductId { get; set; }
    public Guid AttributeId { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public Guid? OptionId { get; set; }
    public bool? ValueBool { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class VariantAttributeValue
{
    public Guid VariantId { get; set; }
    public Guid AttributeId { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public Guid? OptionId { get; set; }
    public bool? ValueBool { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class ProductMedia
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid AssetId { get; set; }
    public string AltText { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsCover { get; set; }
}

[Audited]
public sealed class CollectionRecord : ITaxonomyRecord
{
    public DateTime? FirstPublishedAt { get; set; }
    public uint Version { get; set; }
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? DescriptionHtml { get; set; }
    public string? BodyHtml { get; set; }
    public string? BodyJson { get; set; }
    public Guid? CoverMediaId { get; set; }
    public string Status { get; set; } = null!;
    public int SortOrder { get; set; }
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public bool RobotsNoindex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CollectionProduct
{
    public Guid CollectionId { get; set; }
    public Guid ProductId { get; set; }
    public int SortOrder { get; set; }
}

[Audited]
public sealed class StockLevel
{
    public Guid VariantId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public long? AvgCostVnd { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class StockMovement
{
    public Guid Id { get; set; }
    public Guid VariantId { get; set; }
    public string MovementType { get; set; } = null!;
    public int Quantity { get; set; }
    public long? UnitCostVnd { get; set; }
    public int OnHandAfter { get; set; }
    public string? RefType { get; set; }
    public Guid? RefId { get; set; }
    public string? Reason { get; set; }
    public string ActorType { get; set; } = null!;
    public Guid? ActorId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

[Audited]
public sealed class Receipt
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string? SupplierName { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = null!;
    public DateTime? ReceivedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public Guid? PostedBy { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public uint Version { get; set; }
}

[Audited]
public sealed class ReceiptLine
{
    public Guid Id { get; set; }
    public Guid ReceiptId { get; set; }
    public Guid VariantId { get; set; }
    public int Quantity { get; set; }
    public long UnitCostVnd { get; set; }
}

[Audited]
public sealed class Stocktake
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string? Note { get; set; }
    public string Status { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class StocktakeLine
{
    public Guid StocktakeId { get; set; }
    public Guid VariantId { get; set; }
    public int CountedQty { get; set; }
    public int SystemQtyAtCount { get; set; }
    public DateTime CountedAt { get; set; }
    public Guid CountedBy { get; set; }
}

[Audited]
public sealed class SalePrice
{
    public Guid Id { get; set; }
    public Guid VariantId { get; set; }
    public long SalePriceVnd { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class Redirect
{
    public string SourcePath { get; set; } = null!;
    public string TargetPath { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public long HitCount { get; set; }
}

