using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed class EfCatalogReferenceSeed(AppDbContext db, IUnitOfWork unitOfWork, IClock clock) : ICatalogReferenceSeed
{
    public Task<Result<CatalogSeedOutcome>> SeedAsync(CancellationToken ct)
        => db.Database.CurrentTransaction is not null ? SeedCore(ct) : unitOfWork.ExecuteAsync(SeedCore, ct);

    private async Task<Result<CatalogSeedOutcome>> SeedCore(CancellationToken token)
    {
        // Same lock order as Admin mutations; held through SaveChanges/COMMIT by the UoW.
        await EfCatalogReference.LockAsync(db, token);
        var types = await db.Set<ProductType>().ToArrayAsync(token);
        var attrs = await db.Set<AttributeDefinition>().ToArrayAsync(token);
        var options = await db.Set<AttributeOption>().ToArrayAsync(token);
        var materials = await db.Set<Material>().ToArrayAsync(token);
        var typeCount = 0; var attrCount = 0; var optionCount = 0; var materialCount = 0;
        var now = clock.UtcNow.UtcDateTime;
        foreach (var item in CatalogReferenceDefaults.Types())
        {
            if (types.Any(x => x.Id != item.Id && x.Code == item.Code)) return Conflict();
            if (types.Any(x => x.Id == item.Id)) continue;
            item.CreatedAt = item.UpdatedAt = now; db.Add(item); typeCount++;
        }
        foreach (var item in CatalogReferenceDefaults.Attributes())
        {
            if (attrs.Any(x => x.Id != item.Id && x.ProductTypeId == item.ProductTypeId && x.Code == item.Code)) return Conflict();
            var existing = attrs.SingleOrDefault(x => x.Id == item.Id);
            if (existing is not null)
            {
                if (existing.ProductTypeId != item.ProductTypeId) return Conflict();
                continue;
            }
            db.Add(item); attrCount++;
        }
        foreach (var item in CatalogReferenceDefaults.Options())
        {
            if (options.Any(x => x.Id != item.Id && x.AttributeId == item.AttributeId && x.Code == item.Code)) return Conflict();
            var existing = options.SingleOrDefault(x => x.Id == item.Id);
            if (existing is not null)
            {
                if (existing.AttributeId != item.AttributeId) return Conflict();
                continue;
            }
            if (attrs.SingleOrDefault(x => x.Id == item.AttributeId) is { DataType: not "option" }) return Conflict();
            db.Add(item); optionCount++;
        }
        foreach (var item in CatalogReferenceDefaults.Materials())
        {
            if (materials.Any(x => x.Id != item.Id && (x.SkuCode == item.SkuCode || x.Slug == item.Slug))) return Conflict();
            if (materials.Any(x => x.Id == item.Id)) continue;
            item.CreatedAt = item.UpdatedAt = now; db.Add(item); materialCount++;
        }
        return Result.Success(new CatalogSeedOutcome(typeCount, attrCount, optionCount, materialCount));
    }
    private static Result<CatalogSeedOutcome> Conflict() => Result.Failure<CatalogSeedOutcome>(CatalogRules.Conflict("catalog_seed_identity_conflict"));
}
