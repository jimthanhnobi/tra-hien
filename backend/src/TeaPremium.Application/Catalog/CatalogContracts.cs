using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Application.Results;

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
