using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

internal sealed class EfProductImportReference(AppDbContext db) : IProductImportReference
{
    public Task<ImportType?> TypeAsync(string code, CancellationToken ct) =>
        db.Set<ProductType>().Where(x => x.Code == code && x.IsActive).Select(x => new ImportType(x.Id, x.SkuPrefix)).SingleOrDefaultAsync(ct);

    public Task<ImportMaterial?> MaterialAsync(string slug, CancellationToken ct) =>
        db.Set<Material>().Where(x => x.Slug == slug).Select(x => new ImportMaterial(x.Id, x.SkuCode)).SingleOrDefaultAsync(ct);

    public async Task<Guid?> CategoryAsync(string slug, bool write, CancellationToken ct)
    {
        var query = write
            ? db.Set<Category>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.categories WHERE slug={slug} FOR SHARE")
            : db.Set<Category>().Where(x => x.Slug == slug);
        return (await query.AsNoTracking().ToArrayAsync(ct)).SingleOrDefault()?.Id;
    }

    public async Task<Guid?> ArtisanAsync(string slug, bool write, CancellationToken ct)
    {
        var query = write
            ? db.Set<Artisan>().FromSqlInterpolated($"SELECT *, xmin FROM catalog.artisans WHERE slug={slug} FOR SHARE")
            : db.Set<Artisan>().Where(x => x.Slug == slug);
        return (await query.AsNoTracking().ToArrayAsync(ct)).SingleOrDefault()?.Id;
    }

    public async Task<IReadOnlyDictionary<string, ImportAttributeReference>> AttributesAsync(Guid type, CancellationToken ct)
    {
        var definitions = await db.Set<AttributeDefinition>().AsNoTracking().Where(x => x.ProductTypeId == type).ToArrayAsync(ct);
        var ids = definitions.Select(x => x.Id).ToArray();
        var options = await db.Set<AttributeOption>().AsNoTracking().Where(x => ids.Contains(x.AttributeId)).ToArrayAsync(ct);
        var byDefinition = options.ToLookup(x => x.AttributeId);
        return definitions.ToDictionary(x => x.Code,
            x => new ImportAttributeReference(new(x.Id, type, x.DataType, x.Scope, x.IsActive),
                byDefinition[x.Id].ToDictionary(o => o.Code, o => new TypedAttributeOption(o.Id, x.Id, o.IsActive), StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    public Task<string[]> ExistingSkusAsync(string[] skus, CancellationToken ct) => skus.Length == 0 ? Task.FromResult(Array.Empty<string>())
        : db.Set<Variant>().Where(x => skus.Contains(x.Sku)).Select(x => x.Sku).ToArrayAsync(ct);
}
