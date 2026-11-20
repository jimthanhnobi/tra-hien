using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Catalog;

// P1-05 / FR-CAT-04..07. These aggregate snapshots never contain cost, stock or storage keys.
public sealed class ProductData
{
    public Guid Id { get; set; }
    public string SkuBase { get; set; } = "";
    public Guid ProductTypeId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
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
    public string Status { get; set; } = "draft";
    public DateTime? PublishedAt { get; set; }
    public DateTime? FirstPublishedAt { get; set; }
    public string? ArchivedRedirectPath { get; set; }
    public DateTime? SoldOutAt { get; set; }
    public bool SeoKeepAfterSold { get; set; }
    public bool IsFeatured { get; set; }
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public string? CanonicalUrl { get; set; }
    public bool RobotsNoindex { get; set; }
    public Guid? OgMediaId { get; set; }
    public string SearchText { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [JsonIgnore] public uint Version { get; set; }
    public string RowVersion { get => CatalogVersions.Encode(Version); set { if (CatalogVersions.TryDecode(value, out var version)) Version = version; } }
    public bool ThinDescription => DescriptionWordCount < 150;
    public bool Indexable { get; set; }
    public List<Guid> CategoryIds { get; set; } = [];
    public List<Guid> RelatedProductIds { get; set; } = [];
    public List<ProductValue> Attributes { get; set; } = [];
    public List<VariantData> Variants { get; set; } = [];
    public List<ProductImage> Media { get; set; } = [];
    public List<ContentWarning> Warnings { get; set; } = [];
}
public sealed class VariantData
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Name { get; set; }
    public int Position { get; set; }
    public long? PriceVnd { get; set; }
    public bool IsUniquePiece { get; set; }
    public int? WeightG { get; set; }
    public string? PackageDimsCm { get; set; }
    public string? Barcode { get; set; }
    public int LowStockThreshold { get; set; }
    public string Status { get; set; } = "active";
    [JsonIgnore] public uint Version { get; set; }
    public string RowVersion { get => CatalogVersions.Encode(Version); set { if (CatalogVersions.TryDecode(value, out var version)) Version = version; } }
    public List<ProductValue> Attributes { get; set; } = [];
}
public sealed record ProductValue(Guid AttributeId, string? ValueText = null, decimal? ValueNumber = null,
    Guid? OptionId = null, bool? ValueBool = null, JsonElement? ValueJson = null)
{
    public TypedAttributeValue Typed() => new(ValueText, ValueNumber, OptionId, ValueBool, ValueJson?.GetRawText());
}
public sealed class ProductImage
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public Guid? VariantId { get; set; }
    public string AltText { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCover { get; set; }
    public string? Url { get; set; }
}
public sealed record ProductActor(Guid Id, string[] Permissions, bool IsOwner)
{ public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal); }
public sealed record ProductReference(Guid TypeId, string? Prefix, bool IsActive, string? MaterialCode,
    IReadOnlyList<AttributeView> Attributes, IReadOnlyList<TypedAttributeOption> Options);
public sealed record ProductMutation(Guid? Id, string Operation, Guid? ChildId, JsonElement Fields, ProductActor Actor) : ICommand<ProductData>;
public sealed record GetProduct(Guid Id) : IQuery<ProductData>;
public sealed record ListProducts(int Page, int PageSize, string? Q, string? Status, Guid? Type,
    Guid? Category, Guid? Material, Guid? Artisan, string? Stock, ProductActor Actor) : IQuery<CatalogPage<ProductData>>;
public interface IProductStore
{
    Task LockAsync(bool routingWrite, CancellationToken ct);
    Task<ProductData?> LoadAsync(Guid id, bool update, CancellationToken ct);
    Task<ProductReference?> ReferenceAsync(Guid type, Guid? material, CancellationToken ct);
    Task<string> AllocateBaseAsync(ProductReference reference, bool hasMaterial, CancellationToken ct);
    Task<bool> SlugExistsAsync(string slug, Guid id, CancellationToken ct);
    Task<bool> ReferencesExistAsync(ProductData p, CancellationToken ct);
    Task<bool> CategoryVisibleAsync(Guid id, CancellationToken ct);
    Task<bool> PublicImageAsync(Guid asset, CancellationToken ct);
    Task<bool> VariantUsedAsync(Guid id, CancellationToken ct);
    Task<string?> ArchiveTargetAsync(ProductData p, CancellationToken ct);
    Task SaveAsync(ProductData p, bool create, ProductActor actor, string? noindexReason, CancellationToken ct);
    Task<CatalogPage<ProductData>> ListAsync(ListProducts query, CancellationToken ct);
}
public sealed class ProductMutationValidator : AbstractValidator<ProductMutation>
{
    public ProductMutationValidator()
    {
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Fields).Must(x => x.ValueKind == JsonValueKind.Object && x.GetRawText().Length <= 1_000_000 && CatalogInput.WithoutNul(x) && UniqueKeys(x) && ProductRules.ValidControlFields(x));
    }
    private static bool UniqueKeys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count() && value.EnumerateObject().All(x => UniqueKeys(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueKeys),
        _ => true
    };
}
public sealed class ListProductsValidator : AbstractValidator<ListProducts>
{
    public ListProductsValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Q).MaximumLength(200).Must(CatalogInput.WithoutNul);
        RuleFor(x => x.Status).Must(x => x is null or "draft" or "active" or "archived");
        RuleFor(x => x.Stock).Must(x => x is null or "low" or "out" or "in");
    }
}
