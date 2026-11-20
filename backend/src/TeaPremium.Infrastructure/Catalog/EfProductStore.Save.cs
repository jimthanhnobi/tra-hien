using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfProductStore
{
    public async Task SaveAsync(ProductData p, bool create, ProductActor actor, string? noindexReason, CancellationToken ct)
    {
        var row = create ? new Product { Id = p.Id, CreatedAt = p.CreatedAt, CreatedBy = actor.Id } : await db.Set<Product>().SingleAsync(x => x.Id == p.Id, ct);
        var before = create ? null : ToData(row);
        var axisIds = await db.Set<AttributeDefinition>().Where(x => x.ProductTypeId == p.ProductTypeId && x.Scope == "variant" && x.IsActive && x.IsVariantAxis).Select(x => x.Id).ToArrayAsync(ct);
        var oldCategoryIds = create ? [] : await db.Set<ProductCategory>().Where(x => x.ProductId == p.Id).Select(x => x.CategoryId).ToArrayAsync(ct);
        var categorySlugs = await db.Set<Category>().Where(x => oldCategoryIds.Contains(x.Id) || p.CategoryIds.Contains(x.Id)).Select(x => x.Slug).ToArrayAsync(ct);
        var path = "/san-pham/" + p.Slug;
        var oldPath = before is not null && before.Slug != p.Slug ? "/san-pham/" + before.Slug : null;
        string? discoveredTarget = null;
        var discoveredPublic = false;
        if (p.FirstPublishedAt is not null && p.Status == "archived")
        {
            // Discovery is not a graph mutation. Protect the discovered leaf BEFORE graph acquisition.
            var discovery = await db.Set<Redirect>().AsNoTracking().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath, StringComparer.Ordinal, ct);
            discoveredTarget = ResolveArchiveTarget(TaxonomyRules.Redirects(discovery, oldPath, path), p.ArchivedRedirectPath, path);
            discoveredPublic = await PublicArchiveDestinationAsync(discoveredTarget, true, ct);
        }
        var graph = p.FirstPublishedAt is null ? null : await routing.RedirectsAsync(ct);
        if (graph is not null)
        {
            var next = TaxonomyRules.Redirects(graph, oldPath, path);
            if (p.Status == "archived")
            {
                var target = ResolveArchiveTarget(next, p.ArchivedRedirectPath, path);
                if (target != discoveredTarget) throw Rejected("concurrency_conflict");
                // Re-read under the graph lock, without acquiring another aggregate/namespace lock.
                if (!discoveredPublic || !await PublicArchiveDestinationAsync(target, false, ct)) throw InvalidArchiveTarget();
                next = TaxonomyRules.Redirects(next, path, target); p.ArchivedRedirectPath = target;
            }
            await routing.SaveRedirectsAsync(graph, next, ct);
        }
        Copy(p, row); row.UpdatedBy = actor.Id;
        if (create) db.Add(row); else db.Entry(row).Property(x => x.UpdatedAt).IsModified = true;
        await Flush(ct); // parent must exist before a default variant can be allocated
        foreach (var v in p.Variants.OrderBy(x => x.Id))
        {
            var existing = await db.Set<Variant>().SingleOrDefaultAsync(x => x.Id == v.Id && x.ProductId == p.Id, ct);
            if (existing is null)
            {
                existing = new Variant { Id = v.Id, ProductId = p.Id, CreatedAt = p.UpdatedAt };
                Copy(v, existing); existing.UpdatedAt = p.UpdatedAt;
                if (string.IsNullOrEmpty(v.Sku)) await InsertGenerated(existing, p.SkuBase, p.Variants.IndexOf(v) == 0, AxisLabel(v, axisIds), p.Variants.IndexOf(v) + 1, ct);
                else { db.Add(existing); await Flush(ct); }
                v.Sku = existing.Sku;
            }
            else
            { Copy(v, existing); }
            await Values(p.Id, v, ct);
            db.ChangeTracker.DetectChanges();
            if (db.Entry(existing).State == EntityState.Modified || db.ChangeTracker.Entries<VariantAttributeValue>().Any(x => x.Entity.VariantId == v.Id && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            { existing.UpdatedAt = p.UpdatedAt; db.Entry(existing).Property(x => x.UpdatedAt).IsModified = true; }
        }
        await Values(p.Id, null, ct, p.Attributes);
        var categories = await db.Set<ProductCategory>().Where(x => x.ProductId == p.Id).ToArrayAsync(ct);
        db.RemoveRange(categories.Where(x => !p.CategoryIds.Contains(x.CategoryId)));
        foreach (var id in p.CategoryIds.Where(id => categories.All(x => x.CategoryId != id))) db.Add(new ProductCategory { ProductId = p.Id, CategoryId = id });
        var relations = await db.Set<ProductRelation>().Where(x => x.ProductId == p.Id).ToArrayAsync(ct);
        db.RemoveRange(relations.Where(x => !p.RelatedProductIds.Contains(x.RelatedProductId)));
        for (var i = 0; i < p.RelatedProductIds.Count; i++)
        {
            var id = p.RelatedProductIds[i]; var relation = relations.SingleOrDefault(x => x.RelatedProductId == id);
            if (relation is null) db.Add(new ProductRelation { ProductId = p.Id, RelatedProductId = id, SortOrder = i }); else relation.SortOrder = i;
        }
        var media = await db.Set<ProductMedia>().Where(x => x.ProductId == p.Id).ToArrayAsync(ct);
        foreach (var old in media.Where(x => x.IsCover && !p.Media.Any(m => m.Id == x.Id && m.IsCover))) old.IsCover = false;
        await Flush(ct); // non-deferrable partial UNIQUE: remove the old cover before setting the new one
        db.RemoveRange(media.Where(x => p.Media.All(m => m.Id != x.Id)));
        foreach (var image in p.Media)
        {
            var entity = media.SingleOrDefault(x => x.Id == image.Id);
            if (entity is null) { entity = new ProductMedia { Id = image.Id, ProductId = p.Id }; db.Add(entity); }
            entity.AssetId = image.AssetId; entity.VariantId = image.VariantId; entity.AltText = image.AltText; entity.SortOrder = image.SortOrder; entity.IsCover = image.IsCover;
        }
        audit.Record(before, p, noindexReason);
        var tags = new[] { "product:" + p.Slug, "product:" + (before?.Slug ?? p.Slug), "catalog", "sitemap", "feed" }
            .Concat(categorySlugs.Select(slug => "category:" + slug)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var batch in tags.Chunk(200)) outbox.Enqueue(StorefrontInvalidate.Message, JsonSerializer.Serialize(new StorefrontInvalidate(batch)));
        await Flush(ct);
        // Reuse exactly the canonical consumer's SQL, synchronously in the write UoW. Rename producers remain outbox based.
        await RebuildProductSearchTextHandler.RebuildAsync(db, [p.Id], ct);
    }
    private async Task InsertGenerated(Variant entity, string basis, bool first, string? label, int ordinal, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("SKU allocation requires the command transaction.");
        for (var attempt = 0; attempt < ProductRules.SkuAttempts; attempt++)
        {
            entity.Sku = ProductRules.SkuCandidate(basis, first, label, ordinal, attempt);
            if (!ProductRules.ValidSku(entity.Sku)) throw Rejected("sku_allocation_exhausted");
            var prior = db.ChangeTracker.Entries().Select(x => x.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
            await tx.CreateSavepointAsync("sku_candidate", ct);
            db.Add(entity);
            try { await db.SaveChangesAsync(ct); await tx.ReleaseSavepointAsync("sku_candidate", ct); return; }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_variants_sku" })
            {
                await tx.RollbackToSavepointAsync("sku_candidate", ct);
                // SaveChanges' audit interceptor may have added rows. Detach precisely this failed attempt, preserving prior tracked entities.
                foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray()) entry.State = EntityState.Detached;
                await tx.ReleaseSavepointAsync("sku_candidate", ct);
            }
        }
        throw Rejected("sku_allocation_exhausted");
    }
    private static string? AxisLabel(VariantData variant, Guid[] axisIds) => string.Join('-', variant.Attributes.Where(x => axisIds.Contains(x.AttributeId)).OrderBy(x => x.AttributeId).Select(x => x.ValueText ?? x.ValueNumber?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? x.OptionId?.ToString("N")));
    private async Task Values(Guid product, VariantData? variant, CancellationToken ct, List<ProductValue>? productValues = null)
    {
        if (variant is null)
        {
            var old = await db.Set<ProductAttributeValue>().Where(x => x.ProductId == product).ToDictionaryAsync(x => x.AttributeId, ct);
            var next = productValues!; db.RemoveRange(old.Values.Where(x => next.All(v => v.AttributeId != x.AttributeId)));
            foreach (var value in next)
            {
                if (!old.TryGetValue(value.AttributeId, out var row)) { row = new ProductAttributeValue { ProductId = product, AttributeId = value.AttributeId }; db.Add(row); }
                row.ValueText = value.ValueText; row.ValueNumber = value.ValueNumber; row.OptionId = value.OptionId; row.ValueBool = value.ValueBool; row.ValueJson = value.ValueJson?.GetRawText();
            }
        }
        else
        {
            var old = await db.Set<VariantAttributeValue>().Where(x => x.VariantId == variant.Id).ToDictionaryAsync(x => x.AttributeId, ct);
            db.RemoveRange(old.Values.Where(x => variant.Attributes.All(v => v.AttributeId != x.AttributeId)));
            foreach (var value in variant.Attributes)
            {
                if (!old.TryGetValue(value.AttributeId, out var row)) { row = new VariantAttributeValue { VariantId = variant.Id, AttributeId = value.AttributeId }; db.Add(row); }
                row.ValueText = value.ValueText; row.ValueNumber = value.ValueNumber; row.OptionId = value.OptionId; row.ValueBool = value.ValueBool; row.ValueJson = value.ValueJson?.GetRawText();
            }
        }
    }
    private async Task Flush(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Rejected("concurrency_conflict"); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" or "23503" or "23514" } pg)
        { throw new CatalogPersistenceException(new(pg.ConstraintName == "ux_variants_sku" ? "sku_exists" : pg.SqlState == "23505" ? "catalog_unique_conflict" : "validation_failed", "Dữ liệu không hợp lệ hoặc đã tồn tại.", pg.SqlState == "23505" ? ErrorType.Conflict : ErrorType.Validation)); }
    }
    private static CatalogPersistenceException Rejected(string code) => new(new(code, "Không thể hoàn tất thay đổi.", ErrorType.Conflict));
    private static void Copy(VariantData x, Variant row)
    { row.Sku = x.Sku; row.Name = x.Name; row.Position = x.Position; row.PriceVnd = x.PriceVnd; row.IsUniquePiece = x.IsUniquePiece; row.WeightG = x.WeightG; row.PackageDimsCm = x.PackageDimsCm; row.Barcode = x.Barcode; row.LowStockThreshold = x.LowStockThreshold; row.Status = x.Status; }
    private static void Copy(ProductData x, Product row)
    {
        row.SkuBase = x.SkuBase; row.ProductTypeId = x.ProductTypeId; row.Slug = x.Slug; row.Name = x.Name; row.Subtitle = x.Subtitle;
        row.ShortDescription = x.ShortDescription; row.DescriptionJson = x.DescriptionJson; row.DescriptionHtml = x.DescriptionHtml; row.CareHtml = x.CareHtml;
        row.DescriptionWordCount = x.DescriptionWordCount; row.PrimaryCategoryId = x.PrimaryCategoryId; row.MaterialId = x.MaterialId; row.ArtisanId = x.ArtisanId;
        row.OriginNote = x.OriginNote; row.Status = x.Status; row.PublishedAt = x.PublishedAt; row.FirstPublishedAt = x.FirstPublishedAt; row.ArchivedRedirectPath = x.ArchivedRedirectPath;
        row.SoldOutAt = x.SoldOutAt; row.SeoKeepAfterSold = x.SeoKeepAfterSold; row.IsFeatured = x.IsFeatured; row.FocusKeyword = x.FocusKeyword; row.SeoTitle = x.SeoTitle;
        row.SeoDescription = x.SeoDescription; row.CanonicalUrl = x.CanonicalUrl; row.RobotsNoindex = x.RobotsNoindex; row.OgMediaId = x.OgMediaId; row.UpdatedAt = x.UpdatedAt;
        row.SearchText = x.SearchText;
    }
}
