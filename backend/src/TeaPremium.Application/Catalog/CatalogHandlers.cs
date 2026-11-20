using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;

public sealed class CatalogPersistenceException(Error error) : Exception("Catalog write rejected.")
{
    public Error Error { get; } = error;
}

public sealed partial class CatalogWorkflow(ICatalogStore store, ICatalogContent content, IClock clock, IOutboxWriter outbox)
{
    private static readonly string[] Common = ["name", "slug", "status", "focusKeyword", "seoTitle", "seoDescription", "robotsNoindex", "rowVersion", "noindexReason"];
    private static readonly string[] Kinds = ["clay", "metal", "glass", "ceramic", "porcelain", "wood", "stone", "other"];
    private static Error Bad(string field) => new("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation,
        new Dictionary<string, string[]> { [field] = ["Giá trị không hợp lệ."] });
    private static Error Missing => new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound);
    private static Error Stale => new("concurrency_conflict", "Dữ liệu đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);
    private static Error Forbidden => new("forbidden", "Bạn không có quyền thực hiện thao tác này.", ErrorType.Forbidden);

    public async Task<Result<CatalogView>> SaveAsync(SaveCatalog command, CancellationToken ct)
    {
        if (!command.Actor.Has("catalog.write")) return Result.Failure<CatalogView>(Forbidden);
        var create = command.Id is null;
        var fields = command.Fields;
        if (!AllowedFields(command.Kind, fields)) return Result.Failure<CatalogView>(Bad("fields"));
        var version = 0u;
        if (!create && (!fields.TryGetProperty("rowVersion", out var rv) || rv.ValueKind != JsonValueKind.String || !CatalogVersions.TryDecode(rv.GetString(), out version)))
            return Result.Failure<CatalogView>(Bad("rowVersion"));
        await store.PrepareWriteAsync(command.Kind, ct);
        // Namespace lock is only needed by routing/hierarchy/state mutations; pure metadata edits lock their aggregate row.
        if (create || fields.TryGetProperty("slug", out _) || fields.TryGetProperty("parentId", out _) || fields.TryGetProperty("status", out _))
            await store.LockNamespaceAsync(command.Kind, ct);
        var entry = create ? new CatalogEntry
        {
            Id = Guid.CreateVersion7(),
            Status = command.Kind is CatalogKind.Category or CatalogKind.Collection ? "hidden" : "draft",
            CreatedAt = clock.UtcNow.UtcDateTime
        } : await store.LoadAsync(command.Kind, command.Id!.Value, true, ct);
        if (entry is null) return Result.Failure<CatalogView>(Missing);
        if (!create && entry.Version != version) return Result.Failure<CatalogView>(Stale);
        var oldSlug = entry.Slug; var oldName = entry.Name; var oldStatus = entry.Status; var oldNoindex = entry.RobotsNoindex;
        if (!create && entry.FirstPublishedAt is null && TaxonomyRules.IsPublic(oldStatus))
            return Result.Failure<CatalogView>(new("publication_history_unverified", "Cần xác minh lịch sử công khai trước khi sửa.", ErrorType.Conflict));
        if (!Apply(entry, fields)) return Result.Failure<CatalogView>(Bad("fields"));
        if (string.IsNullOrWhiteSpace(entry.Name)) return Result.Failure<CatalogView>(Bad("name"));
        if (command.Kind is CatalogKind.Material or CatalogKind.Artisan)
        {
            if (entry.Status is not ("draft" or "published" or "archived")) return Result.Failure<CatalogView>(Bad("status"));
            if (entry.Status == "published" && (create || oldStatus != "published")) return Result.Failure<CatalogView>(Forbidden);
        }
        else if (entry.Status is not ("hidden" or "visible")) return Result.Failure<CatalogView>(Bad("status"));
        if (command.Kind == CatalogKind.Material && (!Kinds.Contains(entry.Kind, StringComparer.Ordinal) || entry.SkuCode is not null && (!SkuPattern().IsMatch(entry.SkuCode) || entry.SkuCode == "XX")))
            return Result.Failure<CatalogView>(Bad("kind/skuCode"));
        if (entry.SkuCode is not null && await store.SkuExistsAsync(entry.SkuCode, entry.Id, ct))
            return Result.Failure<CatalogView>(new("sku_code_exists", "Mã chất liệu đã tồn tại.", ErrorType.Conflict));
        if (create && !fields.TryGetProperty("slug", out _))
        {
            var basis = TaxonomyRules.Slug(entry.Name);
            if (!TaxonomyRules.ValidSlug(basis)) return Result.Failure<CatalogView>(Bad("slug"));
            var found = false;
            for (var i = 1; i <= 1000; i++)
            {
                entry.Slug = TaxonomyRules.Candidate(basis, i);
                if (!await store.SlugExistsAsync(command.Kind, entry.Slug, entry.Id, ct)) { found = true; break; }
            }
            if (!found) return Result.Failure<CatalogView>(new("slug_allocation_exhausted", "Không thể cấp đường dẫn duy nhất.", ErrorType.Conflict));
        }
        if (!TaxonomyRules.ValidSlug(entry.Slug)) return Result.Failure<CatalogView>(Bad("slug"));
        if (await store.SlugExistsAsync(command.Kind, entry.Slug, entry.Id, ct)) return Result.Failure<CatalogView>(new("slug_exists", "Đường dẫn đã tồn tại.", ErrorType.Conflict));
        if (command.Kind == CatalogKind.Category)
        {
            var parents = new Dictionary<Guid, Guid?>(await store.ParentsAsync(ct)) { [entry.Id] = entry.ParentId };
            if (!TaxonomyRules.ValidTree(parents)) return Result.Failure<CatalogView>(Bad("parentId"));
        }
        var media = entry.CoverMediaId ?? entry.PortraitMediaId;
        if (media is { } asset && !await store.PublicImageAsync(asset, ct)) return Result.Failure<CatalogView>(Bad("mediaId"));
        var reason = fields.TryGetProperty("noindexReason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
        if (entry.RobotsNoindex && !oldNoindex && command.Kind == CatalogKind.Category && string.IsNullOrWhiteSpace(reason))
            return Result.Failure<CatalogView>(Bad("noindexReason"));
        try
        {
            Sanitize(entry);
            if (TaxonomyRules.IsPublic(entry.Status)) entry.FirstPublishedAt ??= clock.UtcNow.UtcDateTime;
            entry.UpdatedAt = clock.UtcNow.UtcDateTime;
            await RoutingAsync(command.Kind, create ? null : oldSlug, entry, ct);
            await store.PersistAsync(command.Kind, entry, create, ct);
            if (entry.RobotsNoindex && !oldNoindex && command.Kind == CatalogKind.Category) store.NoindexReason(entry.Id, command.Kind, reason!);
            if (!create && oldName != entry.Name && command.Kind is CatalogKind.Material or CatalogKind.Artisan)
            {
                var ids = await store.ReferencingProductsAsync(command.Kind, entry.Id, ct);
                foreach (var batch in ids.Distinct().Order().Chunk(200))
                    outbox.Enqueue(RebuildProductSearchText.Message, JsonSerializer.Serialize(new RebuildProductSearchText(batch)));
            }
            await store.FlushAsync(ct);
            return Result.Success(await ViewAsync(entry, command.Kind, ct));
        }
        catch (CatalogPersistenceException exception) { return Result.Failure<CatalogView>(exception.Error); }
        catch (ArgumentException) { return Result.Failure<CatalogView>(Bad("content")); }
        catch (JsonException) { return Result.Failure<CatalogView>(Bad("content")); }
    }

    public async Task<Result<CatalogPublication>> PublishAsync(PublishCatalog command, CancellationToken ct)
    {
        if (!command.Actor.Has("catalog.publish")) return Result.Failure<CatalogPublication>(Forbidden);
        await store.LockNamespaceAsync(command.Kind, ct);
        var entry = await store.LoadAsync(command.Kind, command.Id, true, ct);
        if (entry is null) return Result.Failure<CatalogPublication>(Missing);
        if (!CatalogVersions.TryDecode(command.RowVersion, out var version)) return Result.Failure<CatalogPublication>(Bad("rowVersion"));
        if (entry.Version != version) return Result.Failure<CatalogPublication>(Stale);
        if (entry.Status == "published" && entry.FirstPublishedAt is null)
            return Result.Failure<CatalogPublication>(new("publication_history_unverified", "Cần xác minh lịch sử công khai.", ErrorType.Conflict));
        if (string.IsNullOrWhiteSpace(entry.Name) || !TaxonomyRules.ValidSlug(entry.Slug)
            || command.Kind == CatalogKind.Material && (!Kinds.Contains(entry.Kind, StringComparer.Ordinal)
                || entry.SkuCode is not null && (!SkuPattern().IsMatch(entry.SkuCode) || entry.SkuCode == "XX")))
            return Result.Failure<CatalogPublication>(Bad("fields"));
        var media = entry.CoverMediaId ?? entry.PortraitMediaId;
        if (media is { } asset && !await store.PublicImageAsync(asset, ct)) return Result.Failure<CatalogPublication>(Bad("mediaId"));
        try
        {
            Sanitize(entry);
            entry.Status = "published"; entry.FirstPublishedAt ??= clock.UtcNow.UtcDateTime; entry.UpdatedAt = clock.UtcNow.UtcDateTime;
            await RoutingAsync(command.Kind, null, entry, ct);
            await store.PersistAsync(command.Kind, entry, false, ct);
            await store.FlushAsync(ct);
            var words = content.Sanitize(null, command.Kind == CatalogKind.Material ? entry.DescriptionHtml : entry.BioHtml).Words;
            ContentWarning[] warnings = words >= 400 ? [] : [new("content_below_recommended_words", command.Kind == CatalogKind.Material ? "descriptionHtml" : "bioHtml", words, 400)];
            return Result.Success(new CatalogPublication(entry.Id, entry.Status, CatalogVersions.Encode(entry.Version), warnings));
        }
        catch (CatalogPersistenceException exception) { return Result.Failure<CatalogPublication>(exception.Error); }
        catch (ArgumentException) { return Result.Failure<CatalogPublication>(Bad("content")); }
        catch (JsonException) { return Result.Failure<CatalogPublication>(Bad("content")); }
    }
    private void Sanitize(CatalogEntry entry)
    {
        if (entry.DescriptionJson is not null || entry.DescriptionHtml is not null)
        { var description = content.Sanitize(entry.DescriptionJson, entry.DescriptionHtml); entry.DescriptionJson = description.Json; entry.DescriptionHtml = description.Html; }
        if (entry.BioJson is not null || entry.BioHtml is not null)
        { var bio = content.Sanitize(entry.BioJson, entry.BioHtml); entry.BioJson = bio.Json; entry.BioHtml = bio.Html; }
        if (entry.BodyJson is not null || entry.BodyHtml is not null)
        { var body = content.Sanitize(entry.BodyJson, entry.BodyHtml); entry.BodyJson = body.Json; entry.BodyHtml = body.Html; }
    }
    private async Task RoutingAsync(CatalogKind kind, string? oldSlug, CatalogEntry entry, CancellationToken ct)
    {
        if (entry.FirstPublishedAt is null) return;
        var prefix = TaxonomyRules.Prefix(kind);
        var before = await store.RedirectsAsync(ct);
        var after = TaxonomyRules.Redirects(before, oldSlug is not null && oldSlug != entry.Slug ? prefix + oldSlug : null, prefix + entry.Slug);
        await store.SaveRedirectsAsync(before, after, ct);
    }
    public async Task<CatalogView> ViewAsync(CatalogEntry entry, CatalogKind kind, CancellationToken ct)
    {
        var plain = content.Sanitize(null, kind == CatalogKind.Artisan ? entry.BioHtml : entry.DescriptionHtml).Text;
        var seo = CatalogSeo.Resolve(entry, kind, plain, await store.BrandAsync(ct));
        return new(entry.Id, CatalogVersions.Encode(entry.Version), entry.Name, entry.Slug, entry.Status, entry.FirstPublishedAt,
            entry.ParentId, entry.SortOrder, entry.Kind, entry.SkuCode, entry.Summary, entry.Title, entry.DescriptionHtml, entry.DescriptionJson,
            entry.BioHtml, entry.BioJson, entry.BodyHtml, entry.BodyJson, entry.CoverMediaId, entry.PortraitMediaId,
            entry.FocusKeyword, entry.SeoTitle, entry.SeoDescription, entry.RobotsNoindex, seo.Title, seo.Description);
    }
    public async Task<Result<CollectionProductList>> PutProductsAsync(PutCollectionProducts command, CancellationToken ct)
    {
        if (!command.Actor.Has("catalog.write")) return Result.Failure<CollectionProductList>(Forbidden);
        var entry = await store.LoadAsync(CatalogKind.Collection, command.Id, true, ct);
        if (entry is null) return Result.Failure<CollectionProductList>(Missing);
        if (!CatalogVersions.TryDecode(command.RowVersion, out var version)) return Result.Failure<CollectionProductList>(Bad("rowVersion"));
        if (entry.Version != version) return Result.Failure<CollectionProductList>(Stale);
        if (!await store.ProductsExistAsync(command.Items.Select(i => i.ProductId).ToArray(), ct)) return Result.Failure<CollectionProductList>(Bad("items"));
        try
        {
            await store.ReplaceProductsAsync(entry.Id, command.Items, ct);
            entry.UpdatedAt = clock.UtcNow.UtcDateTime;
            await store.PersistAsync(CatalogKind.Collection, entry, false, ct); await store.FlushAsync(ct);
            return Result.Success(new CollectionProductList(entry.Id, CatalogVersions.Encode(entry.Version), command.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.ProductId).ToArray()));
        }
        catch (CatalogPersistenceException exception) { return Result.Failure<CollectionProductList>(exception.Error); }
    }
    private static bool AllowedFields(CatalogKind kind, JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) return false;
        string[] extra = kind switch
        {
            CatalogKind.Category => ["parentId", "sortOrder", "coverMediaId", "descriptionHtml", "bodyHtml", "bodyJson"],
            CatalogKind.Collection => ["sortOrder", "coverMediaId", "descriptionHtml", "bodyHtml", "bodyJson"],
            CatalogKind.Material => ["skuCode", "kind", "summary", "sortOrder", "coverMediaId", "descriptionHtml", "descriptionJson"],
            CatalogKind.Artisan => ["title", "portraitMediaId", "bioHtml", "bioJson"],
            _ => []
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return fields.EnumerateObject().All(p => seen.Add(p.Name) && (Common.Contains(p.Name, StringComparer.Ordinal) || extra.Contains(p.Name, StringComparer.Ordinal)));
    }
    private static bool Apply(CatalogEntry entry, JsonElement fields)
    {
        foreach (var field in fields.EnumerateObject())
        {
            if (field.Name is "rowVersion" or "noindexReason") continue;
            var property = typeof(CatalogEntry).GetProperty(char.ToUpperInvariant(field.Name[0]) + field.Name[1..], BindingFlags.Public | BindingFlags.Instance)!;
            object? value;
            if (field.Value.ValueKind == JsonValueKind.Null)
            {
                if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null) return false;
                value = null;
            }
            else if (field.Name.EndsWith("Json", StringComparison.Ordinal))
            {
                if (field.Value.ValueKind != JsonValueKind.Object) return false;
                value = field.Value.GetRawText();
            }
            else if (property.PropertyType == typeof(string))
            {
                if (field.Value.ValueKind != JsonValueKind.String) return false;
                value = field.Value.GetString();
                if (field.Name is "name" or "slug" or "skuCode" or "kind" or "seoTitle" or "seoDescription" or "focusKeyword" && ((string?)value)?.Any(char.IsControl) == true) return false;
            }
            else if (property.PropertyType == typeof(Guid?))
            {
                if (field.Value.ValueKind != JsonValueKind.String || !field.Value.TryGetGuid(out var id)) return false;
                value = id;
            }
            else if (property.PropertyType == typeof(int))
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number)) return false;
                value = number;
            }
            else
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                value = field.Value.GetBoolean();
            }
            property.SetValue(entry, value);
        }
        // HTML-only edits replace the corresponding JSON source; omitted fields remain untouched.
        if (fields.TryGetProperty("descriptionHtml", out _) && !fields.TryGetProperty("descriptionJson", out _)) entry.DescriptionJson = null;
        if (fields.TryGetProperty("bioHtml", out _) && !fields.TryGetProperty("bioJson", out _)) entry.BioJson = null;
        if (fields.TryGetProperty("bodyHtml", out _) && !fields.TryGetProperty("bodyJson", out _)) entry.BodyJson = null;
        return true;
    }
    [GeneratedRegex("\\A[A-Z]{2,3}\\z", RegexOptions.CultureInvariant)] private static partial Regex SkuPattern();
}

public sealed class SaveCatalogHandler(CatalogWorkflow flow) : ICommandHandler<SaveCatalog, CatalogView>
{ public Task<Result<CatalogView>> Handle(SaveCatalog command, CancellationToken cancellationToken) => flow.SaveAsync(command, cancellationToken); }
public sealed class PublishCatalogHandler(CatalogWorkflow flow) : ICommandHandler<PublishCatalog, CatalogPublication>
{ public Task<Result<CatalogPublication>> Handle(PublishCatalog command, CancellationToken cancellationToken) => flow.PublishAsync(command, cancellationToken); }
public sealed class PutCollectionProductsHandler(CatalogWorkflow flow) : ICommandHandler<PutCollectionProducts, CollectionProductList>
{ public Task<Result<CollectionProductList>> Handle(PutCollectionProducts command, CancellationToken cancellationToken) => flow.PutProductsAsync(command, cancellationToken); }
public sealed class GetCatalogHandler(ICatalogStore store, CatalogWorkflow flow) : IQueryHandler<GetCatalog, CatalogView>
{
    public async Task<Result<CatalogView>> Handle(GetCatalog query, CancellationToken cancellationToken)
    {
        var entry = await store.LoadAsync(query.Kind, query.Id, false, cancellationToken);
        return entry is null ? Result.Failure<CatalogView>(new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound)) : Result.Success(await flow.ViewAsync(entry, query.Kind, cancellationToken));
    }
}
public sealed class ListCatalogHandler(ICatalogStore store, CatalogWorkflow flow) : IQueryHandler<ListCatalog, CatalogPage>
{
    public async Task<Result<CatalogPage>> Handle(ListCatalog query, CancellationToken cancellationToken)
    {
        var (items, total) = await store.ListAsync(query, cancellationToken);
        var views = new List<CatalogView>(); foreach (var entry in items) views.Add(await flow.ViewAsync(entry, query.Kind, cancellationToken));
        return Result.Success(new CatalogPage(views, query.Page, query.PageSize, total));
    }
}
public sealed class GetCollectionProductsHandler(ICatalogStore store) : IQueryHandler<GetCollectionProducts, CollectionProductList>
{
    public async Task<Result<CollectionProductList>> Handle(GetCollectionProducts query, CancellationToken cancellationToken)
    {
        var entry = await store.LoadAsync(CatalogKind.Collection, query.Id, false, cancellationToken);
        return entry is null ? Result.Failure<CollectionProductList>(new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound))
            : Result.Success(new CollectionProductList(entry.Id, CatalogVersions.Encode(entry.Version), await store.ProductsAsync(entry.Id, cancellationToken)));
    }
}
