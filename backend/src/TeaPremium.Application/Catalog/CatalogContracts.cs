using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;


// P1-03 / FR-CAT-01, DEC-04. Writable metadata is separate from server-owned identity.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductTypeInput(string Code, string Name, string? SkuPrefix = null,
    string? Description = null, string? GoogleProductCategory = null, int SortOrder = 0, bool IsActive = true);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttributeInput(string Code, string Name, string DataType, string? Unit = null,
    string Scope = "product", bool IsVariantAxis = false, bool IsRequiredForPublish = false,
    bool IsFilterable = false, bool IsVisible = true, int SortOrder = 0, bool IsActive = true);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionInput(string Code, string Label, int SortOrder = 0, bool IsActive = true);
public sealed record ProductTypeView(Guid Id, string Code, string? SkuPrefix, string Name,
    string? Description, string? GoogleProductCategory, int SortOrder, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt, string RowVersion);
public sealed record AttributeView(Guid Id, Guid ProductTypeId, string Code, string Name,
    string DataType, string? Unit, string Scope, bool IsVariantAxis, bool IsRequiredForPublish,
    bool IsFilterable, bool IsVisible, int SortOrder, bool IsActive, string RowVersion);
public sealed record OptionView(Guid Id, Guid AttributeId, string Code, string Label, int SortOrder,
    bool IsActive, string RowVersion);
public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalItems);
public sealed record CatalogSeedOutcome(int ProductTypesAdded, int AttributesAdded, int OptionsAdded, int MaterialsAdded);

// The persistence adapter uses the existing IUnitOfWork for each mutation. No P1-05 value writes.
public interface ICatalogReference
{
    Task<Result<CatalogPage<ProductTypeView>>> TypesAsync(int page, int pageSize, CancellationToken ct);
    Task<Result<ProductTypeView>> TypeAsync(Guid id, CancellationToken ct);
    Task<Result<ProductTypeView>> CreateTypeAsync(ProductTypeInput input, CancellationToken ct);
    Task<Result<ProductTypeView>> PatchTypeAsync(Guid id, JsonElement patch, CancellationToken ct);
    Task<Result<CatalogPage<AttributeView>>> AttributesAsync(Guid typeId, int page, int pageSize, CancellationToken ct);
    Task<Result<AttributeView>> AttributeAsync(Guid typeId, Guid id, CancellationToken ct);
    Task<Result<AttributeView>> CreateAttributeAsync(Guid typeId, AttributeInput input, CancellationToken ct);
    Task<Result<AttributeView>> PatchAttributeAsync(Guid typeId, Guid id, JsonElement patch, CancellationToken ct);
    Task<Result<CatalogPage<OptionView>>> OptionsAsync(Guid typeId, Guid attributeId, int page, int pageSize, CancellationToken ct);
    Task<Result<OptionView>> OptionAsync(Guid typeId, Guid attributeId, Guid id, CancellationToken ct);
    Task<Result<OptionView>> CreateOptionAsync(Guid typeId, Guid attributeId, OptionInput input, CancellationToken ct);
    Task<Result<OptionView>> PatchOptionAsync(Guid typeId, Guid attributeId, Guid id, JsonElement patch, CancellationToken ct);
}
public interface ICatalogReferenceSeed
{
    Task<Result<CatalogSeedOutcome>> SeedAsync(CancellationToken ct);
}


public sealed class CatalogEntry : ITaxonomyRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FocusKeyword { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public bool RobotsNoindex { get; set; }
    public DateTime? FirstPublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? CoverMediaId { get; set; }
    public Guid? PortraitMediaId { get; set; }
    public int SortOrder { get; set; }
    public string? SkuCode { get; set; }
    public string? Kind { get; set; }
    public string? Summary { get; set; }
    public string? Title { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? DescriptionJson { get; set; }
    public string? BioHtml { get; set; }
    public string? BioJson { get; set; }
    public string? BodyHtml { get; set; }
    public string? BodyJson { get; set; }
}
public sealed record CatalogActor(Guid Id, string[] Permissions)
{
    public bool Has(string value) => Permissions.Contains(value, StringComparer.Ordinal);
}
public sealed record SaveCatalog(CatalogKind Kind, Guid? Id, JsonElement Fields, CatalogActor Actor) : ICommand<CatalogView>;
public sealed record PublishCatalog(CatalogKind Kind, Guid Id, string RowVersion, CatalogActor Actor) : ICommand<CatalogPublication>;
public sealed record GetCatalog(CatalogKind Kind, Guid Id) : IQuery<CatalogView>;
public sealed record ListCatalog(CatalogKind Kind, int Page, int PageSize, string? Q, string? Status) : IQuery<CatalogPage>;
public sealed record CollectionProductView(Guid ProductId, int SortOrder);
public sealed record CollectionProductList(Guid Id, string RowVersion, IReadOnlyList<CollectionProductView> Items);
public sealed record GetCollectionProducts(Guid Id) : IQuery<CollectionProductList>;
public sealed record PutCollectionProducts(Guid Id, string RowVersion, IReadOnlyList<CollectionProductView> Items, CatalogActor Actor) : ICommand<CollectionProductList>;
public sealed record ContentWarning(string Code, string Field, int ActualWords, int RecommendedWords);
public sealed record CatalogPublication(Guid Id, string Status, string RowVersion, IReadOnlyList<ContentWarning> Warnings);
public sealed record CatalogView(Guid Id, string RowVersion, string Name, string Slug, string Status,
    DateTime? FirstPublishedAt, Guid? ParentId, int SortOrder, string? Kind, string? SkuCode, string? Summary,
    string? Title, string? DescriptionHtml, string? DescriptionJson, string? BioHtml, string? BioJson,
    string? BodyHtml, string? BodyJson, Guid? CoverMediaId, Guid? PortraitMediaId,
    string? FocusKeyword, string? SeoTitle, string? SeoDescription, bool RobotsNoindex,
    string ResolvedSeoTitle, string ResolvedSeoDescription);
public sealed record CatalogPage(IReadOnlyList<CatalogView> Items, int Page, int PageSize, long TotalItems);
public sealed record CatalogContent(string? Json, string Html, string Text, int Words);
// PostgreSQL text/jsonb cannot represent U+0000. Inspect decoded JSON strings and
// property names before handlers start a transaction; do not strip or reject other Unicode.
public static class CatalogInput
{
    public static bool WithoutNul(string? value) => value is null || !value.Contains('\0');
    public static bool WithoutNul(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => WithoutNul(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().All(p => WithoutNul(p.Name) && WithoutNul(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(WithoutNul),
        _ => true
    };
}
public interface ICatalogContent
{
    CatalogContent Sanitize(string? json, string? html);
}

public interface ICatalogStore
{
    Task LockNamespaceAsync(CatalogKind kind, CancellationToken ct);
    Task<CatalogEntry?> LoadAsync(CatalogKind kind, Guid id, bool forUpdate, CancellationToken ct);
    Task<(IReadOnlyList<CatalogEntry> Items, long Total)> ListAsync(ListCatalog query, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, Guid?>> ParentsAsync(CancellationToken ct);
    Task<bool> SlugExistsAsync(CatalogKind kind, string slug, Guid id, CancellationToken ct);
    Task<bool> SkuExistsAsync(string code, Guid id, CancellationToken ct);
    Task<bool> PublicImageAsync(Guid id, CancellationToken ct);
    Task<string?> BrandAsync(CancellationToken ct);
    Task PersistAsync(CatalogKind kind, CatalogEntry entry, bool create, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> RedirectsAsync(CancellationToken ct);
    Task SaveRedirectsAsync(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, CancellationToken ct);
    Task<IReadOnlyList<CollectionProductView>> ProductsAsync(Guid id, CancellationToken ct);
    Task<bool> ProductsExistAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task ReplaceProductsAsync(Guid id, IReadOnlyList<CollectionProductView> items, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ReferencingProductsAsync(CatalogKind kind, Guid id, CancellationToken ct);
    void NoindexReason(Guid id, CatalogKind kind, string reason);
    Task FlushAsync(CancellationToken ct);
}

public static class CatalogVersions
{
    public static string Encode(uint version) => Convert.ToBase64String(BitConverter.GetBytes(version));
    public static bool TryDecode(string? text, out uint version)
    {
        version = 0;
        Span<byte> bytes = stackalloc byte[4];
        if (text is null || !Convert.TryFromBase64String(text, bytes, out var written) || written != 4) return false;
        version = BitConverter.ToUInt32(bytes);
        return version != 0;
    }
}
public sealed class SaveCatalogValidator : AbstractValidator<SaveCatalog>
{
    public SaveCatalogValidator()
    {
        RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Fields).Must(x => x.ValueKind == JsonValueKind.Object && x.GetRawText().Length <= 1_000_000);
        RuleFor(x => x.Fields).Must(CatalogInput.WithoutNul);
    }
}
public sealed class ListCatalogValidator : AbstractValidator<ListCatalog>
{
    public ListCatalogValidator()
    {
        RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100); RuleFor(x => x.Q).MaximumLength(200).Must(CatalogInput.WithoutNul);
        RuleFor(x => x.Status).Must((x, status) => status is null || (x.Kind is CatalogKind.Category or CatalogKind.Collection
            ? status is "visible" or "hidden" : status is "draft" or "published" or "archived"));
    }
}
public sealed class PublishCatalogValidator : AbstractValidator<PublishCatalog>
{
    public PublishCatalogValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Kind).Must(x => x is CatalogKind.Material or CatalogKind.Artisan);
        RuleFor(x => x.RowVersion).Must(x => CatalogVersions.TryDecode(x, out _));
    }
}
public sealed class PutCollectionProductsValidator : AbstractValidator<PutCollectionProducts>
{
    public PutCollectionProductsValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.RowVersion).Must(x => CatalogVersions.TryDecode(x, out _));
        RuleFor(x => x.Items).NotNull().Must(x => x is not null && x.Count <= 5000 && x.All(i => i is not null && i.ProductId != Guid.Empty)
            && x.Select(i => i.ProductId).Distinct().Count() == x.Count);
    }
}
