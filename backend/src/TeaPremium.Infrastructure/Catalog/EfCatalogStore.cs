using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed class EfCatalogStore(AppDbContext db, IClock clock, IAuditContext audit, IDataProtectionProvider protection) : ICatalogStore
{
    private readonly Dictionary<(CatalogKind Kind, Guid Id), (ITaxonomyRecord Row, CatalogEntry Entry)> pending = [];
    private Guid? redirectSnapshotTransaction;
    public async Task LockNamespaceAsync(CatalogKind kind, CancellationToken ct)
    {
        // Transaction scoped, per taxonomy namespace; no process/global catalog lock.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(8104, {(int)kind})", ct);
    }
    public Task<CatalogEntry?> LoadAsync(CatalogKind kind, Guid id, bool forUpdate, CancellationToken ct) => kind switch
    {
        CatalogKind.Category => LoadAsync<Category>(kind, "categories", id, forUpdate, ct),
        CatalogKind.Collection => LoadAsync<CollectionRecord>(kind, "collections", id, forUpdate, ct),
        CatalogKind.Material => LoadAsync<Material>(kind, "materials", id, forUpdate, ct),
        CatalogKind.Artisan => LoadAsync<Artisan>(kind, "artisans", id, forUpdate, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private async Task<CatalogEntry?> LoadAsync<T>(CatalogKind kind, string table, Guid id, bool update, CancellationToken ct) where T : class, ITaxonomyRecord
    {
        var sql = table switch
        {
            "categories" => "SELECT *, xmin FROM catalog.categories WHERE id = @id FOR UPDATE",
            "collections" => "SELECT *, xmin FROM catalog.collections WHERE id = @id FOR UPDATE",
            "materials" => "SELECT *, xmin FROM catalog.materials WHERE id = @id FOR UPDATE",
            "artisans" => "SELECT *, xmin FROM catalog.artisans WHERE id = @id FOR UPDATE",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var row = update
            ? (await db.Set<T>().FromSqlRaw(sql, new NpgsqlParameter("id", id)).ToArrayAsync(ct)).SingleOrDefault()
            : await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return null;
        var entry = ToEntry(row);
        if (update) pending[(kind, id)] = (row, entry);
        return entry;
    }
    public Task<(IReadOnlyList<CatalogEntry> Items, long Total)> ListAsync(ListCatalog query, CancellationToken ct) => query.Kind switch
    {
        CatalogKind.Category => ListAsync<Category>(query, ct), CatalogKind.Collection => ListAsync<CollectionRecord>(query, ct),
        CatalogKind.Material => ListAsync<Material>(query, ct), CatalogKind.Artisan => ListAsync<Artisan>(query, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };
    private async Task<(IReadOnlyList<CatalogEntry> Items, long Total)> ListAsync<T>(ListCatalog query, CancellationToken ct) where T : class, ITaxonomyRecord
    {
        var rows = db.Set<T>().AsNoTracking();
        if (query.Status is not null) rows = rows.Where(x => x.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Q)) rows = rows.Where(x => x.Name.Contains(query.Q));
        var total = await rows.LongCountAsync(ct);
        var ordered = typeof(T) == typeof(Artisan) ? rows.OrderBy(x => x.Name).ThenBy(x => x.Id)
            : rows.OrderBy(x => EF.Property<int>(x, "SortOrder")).ThenBy(x => x.Name).ThenBy(x => x.Id);
        var page = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        return (page.Select(ToEntry).ToArray(), total);
    }
    public async Task<IReadOnlyDictionary<Guid, Guid?>> ParentsAsync(CancellationToken ct) => await db.Set<Category>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ParentId, ct);
    public Task<bool> SlugExistsAsync(CatalogKind kind, string slug, Guid id, CancellationToken ct) => kind switch
    {
        CatalogKind.Category => db.Set<Category>().AnyAsync(x => x.Slug == slug && x.Id != id, ct),
        CatalogKind.Collection => db.Set<CollectionRecord>().AnyAsync(x => x.Slug == slug && x.Id != id, ct),
        CatalogKind.Material => db.Set<Material>().AnyAsync(x => x.Slug == slug && x.Id != id, ct),
        CatalogKind.Artisan => db.Set<Artisan>().AnyAsync(x => x.Slug == slug && x.Id != id, ct), _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public Task<bool> SkuExistsAsync(string code, Guid id, CancellationToken ct) => db.Set<Material>().AnyAsync(x => x.SkuCode == code && x.Id != id, ct);
    public Task<bool> PublicImageAsync(Guid id, CancellationToken ct) => db.Set<MediaAsset>().AnyAsync(x => x.Id == id && x.Purpose != "evidence"
        && (x.MimeType == "image/jpeg" || x.MimeType == "image/png" || x.MimeType == "image/webp" || x.MimeType == "image/avif"), ct);
    public async Task<string?> BrandAsync(CancellationToken ct)
    {
        var value = await db.Settings.Where(x => x.Key == "seo.brand_name").Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (value is null) return null;
        using var json = JsonDocument.Parse(value);
        return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null;
    }
    public Task PersistAsync(CatalogKind kind, CatalogEntry entry, bool create, CancellationToken ct)
    {
        ITaxonomyRecord row;
        if (create)
        {
            row = kind switch { CatalogKind.Category => new Category(), CatalogKind.Collection => new CollectionRecord(), CatalogKind.Material => new Material(), CatalogKind.Artisan => new Artisan(), _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
            db.Add(row);
            pending[(kind, entry.Id)] = (row, entry);
        }
        else row = pending[(kind, entry.Id)].Row;
        row.Id = entry.Id; row.Name = entry.Name; row.Slug = entry.Slug; row.Status = entry.Status;
        row.FocusKeyword = entry.FocusKeyword; row.SeoTitle = entry.SeoTitle; row.SeoDescription = entry.SeoDescription;
        row.RobotsNoindex = entry.RobotsNoindex; row.FirstPublishedAt = entry.FirstPublishedAt;
        row.CreatedAt = entry.CreatedAt; row.UpdatedAt = entry.UpdatedAt;
        switch (row)
        {
            case Material m:
                m.Kind = entry.Kind!; m.SkuCode = entry.SkuCode; m.Summary = entry.Summary; m.SortOrder = entry.SortOrder;
                m.DescriptionHtml = entry.DescriptionHtml; m.DescriptionJson = entry.DescriptionJson; m.CoverMediaId = entry.CoverMediaId; break;
            case Artisan a:
                a.Title = entry.Title; a.BioHtml = entry.BioHtml; a.BioJson = entry.BioJson; a.PortraitMediaId = entry.PortraitMediaId; break;
            case Category c:
                c.ParentId = entry.ParentId; c.DescriptionHtml = entry.DescriptionHtml; c.BodyHtml = entry.BodyHtml; c.BodyJson = entry.BodyJson;
                c.CoverMediaId = entry.CoverMediaId; c.SortOrder = entry.SortOrder; break;
            case CollectionRecord c:
                c.DescriptionHtml = entry.DescriptionHtml; c.BodyHtml = entry.BodyHtml; c.BodyJson = entry.BodyJson;
                c.CoverMediaId = entry.CoverMediaId; c.SortOrder = entry.SortOrder; break;
        }
        if (!create) db.Entry(row).Property(nameof(ITaxonomyRecord.UpdatedAt)).IsModified = true;
        return Task.CompletedTask;
    }
    private static CatalogEntry ToEntry(ITaxonomyRecord row)
    {
        var entry = new CatalogEntry { Id = row.Id, Name = row.Name, Slug = row.Slug, Status = row.Status,
            FocusKeyword = row.FocusKeyword, SeoTitle = row.SeoTitle, SeoDescription = row.SeoDescription, RobotsNoindex = row.RobotsNoindex,
            FirstPublishedAt = row.FirstPublishedAt, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt, Version = row.Version };
        switch (row)
        {
            case Material m:
                entry.Kind = m.Kind; entry.SkuCode = m.SkuCode; entry.Summary = m.Summary; entry.SortOrder = m.SortOrder;
                entry.DescriptionHtml = m.DescriptionHtml; entry.DescriptionJson = m.DescriptionJson; entry.CoverMediaId = m.CoverMediaId; break;
            case Artisan a:
                entry.Title = a.Title; entry.BioHtml = a.BioHtml; entry.BioJson = a.BioJson; entry.PortraitMediaId = a.PortraitMediaId; break;
            case Category c:
                entry.ParentId = c.ParentId; entry.DescriptionHtml = c.DescriptionHtml; entry.BodyHtml = c.BodyHtml; entry.BodyJson = c.BodyJson;
                entry.CoverMediaId = c.CoverMediaId; entry.SortOrder = c.SortOrder; break;
            case CollectionRecord c:
                entry.DescriptionHtml = c.DescriptionHtml; entry.BodyHtml = c.BodyHtml; entry.BodyJson = c.BodyJson;
                entry.CoverMediaId = c.CoverMediaId; entry.SortOrder = c.SortOrder; break;
        }
        return entry;
    }
    public async Task<IReadOnlyDictionary<string, string>> RedirectsAsync(CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Redirect graph reads for mutation require a transaction.");
        // Shared graph, shared lock across ALL prefixes. Order is namespace (when
        // needed) -> owning aggregate row -> graph -> redirect rows. Graph writers
        // never acquire another aggregate/namespace after this point. No external IO.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8104, 100)", ct);
        redirectSnapshotTransaction = transaction.TransactionId;
        return await db.Set<Redirect>().AsNoTracking().ToDictionaryAsync(x => x.SourcePath, x => x.TargetPath, StringComparer.Ordinal, ct);
    }
    public async Task SaveRedirectsAsync(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, CancellationToken ct)
    {
        if (redirectSnapshotTransaction is null || redirectSnapshotTransaction != db.Database.CurrentTransaction?.TransactionId)
            throw new InvalidOperationException("Redirect graph writes require the shared lock and snapshot in the same transaction.");
        var changed = before.Keys.Union(after.Keys, StringComparer.Ordinal).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).ToArray();
        if (changed.Length == 0) return;
        var rows = await db.Set<Redirect>().Where(x => changed.Contains(x.SourcePath)).ToDictionaryAsync(x => x.SourcePath, ct);
        foreach (var source in changed)
        {
            if (!after.TryGetValue(source, out var target)) { if (rows.TryGetValue(source, out var removed)) db.Remove(removed); }
            else if (rows.TryGetValue(source, out var row)) row.TargetPath = target;
            else db.Add(new Redirect { SourcePath = source, TargetPath = target, CreatedAt = clock.UtcNow.UtcDateTime });
        }
    }
    public async Task<IReadOnlyList<CollectionProductView>> ProductsAsync(Guid id, CancellationToken ct) => await db.Set<CollectionProduct>().AsNoTracking().Where(x => x.CollectionId == id)
        .OrderBy(x => x.SortOrder).ThenBy(x => x.ProductId).Select(x => new CollectionProductView(x.ProductId, x.SortOrder)).ToArrayAsync(ct);
    public async Task<bool> ProductsExistAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => await db.Set<Product>().CountAsync(x => ids.Contains(x.Id), ct) == ids.Count;
    public async Task ReplaceProductsAsync(Guid id, IReadOnlyList<CollectionProductView> items, CancellationToken ct)
    {
        var old = await db.Set<CollectionProduct>().Where(x => x.CollectionId == id).ToDictionaryAsync(x => x.ProductId, ct);
        var ids = items.Select(i => i.ProductId).ToHashSet();
        foreach (var row in old.Values.Where(x => !ids.Contains(x.ProductId))) db.Remove(row);
        foreach (var item in items)
            if (old.TryGetValue(item.ProductId, out var row)) row.SortOrder = item.SortOrder;
            else db.Add(new CollectionProduct { CollectionId = id, ProductId = item.ProductId, SortOrder = item.SortOrder });
    }
    public async Task<IReadOnlyList<Guid>> ReferencingProductsAsync(CatalogKind kind, Guid id, CancellationToken ct) => kind == CatalogKind.Material
        ? await db.Set<Product>().Where(x => x.MaterialId == id).Select(x => x.Id).ToArrayAsync(ct)
        : await db.Set<Product>().Where(x => x.ArtisanId == id).Select(x => x.Id).ToArrayAsync(ct);
    public void NoindexReason(Guid id, CatalogKind kind, string reason)
    {
        // Retain the actual reason, protected by the existing key repository/certificate.
        // Free text can contain PII; the ordinary audit response must not expose it in plaintext.
        db.AuditLogs.Add(new AuditLog { Id = Guid.CreateVersion7(), EntityType = "catalog." + (kind == CatalogKind.Category ? "categories" : "collections"),
            EntityId = id, Action = "state_change", ActorType = audit.ActorType, ActorId = audit.ActorId, OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = audit.CorrelationId, Changes = JsonSerializer.Serialize(new { robots_noindex = true,
                reason = protection.CreateProtector("teapremium.catalog.noindex-reason.v1").Protect(reason), reason_format = "data_protection_v1" }) });
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new CatalogPersistenceException(new("concurrency_conflict", "Dữ liệu đã thay đổi.", ErrorType.Conflict)); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" or "23503" or "23514" })
        {
            var state = ((PostgresException)exception.InnerException).SqlState;
            throw new CatalogPersistenceException(new(state == "23505" ? "catalog_unique_conflict" : "validation_failed", "Dữ liệu không hợp lệ hoặc đã tồn tại.", state == "23505" ? ErrorType.Conflict : ErrorType.Validation));
        }
        foreach (var (row, entry) in pending.Values) entry.Version = row.Version;
    }
}
