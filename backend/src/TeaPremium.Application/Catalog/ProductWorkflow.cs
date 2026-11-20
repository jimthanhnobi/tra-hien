using System.Text.Json;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;

public sealed class ProductWorkflow(IProductStore store, ICatalogContent content, IClock clock)
{
    private static readonly string[] ProductFields = ["name", "slug", "productTypeId", "subtitle", "shortDescription", "descriptionHtml", "descriptionJson", "careHtml",
        "primaryCategoryId", "materialId", "artisanId", "originNote", "seoKeepAfterSold", "isFeatured", "focusKeyword", "seoTitle", "seoDescription", "canonicalUrl",
        "robotsNoindex", "ogMediaId", "categoryIds", "relatedProductIds", "attributes", "rowVersion", "noindexReason", "confirmTypeChange", "defaultVariant"];
    private static readonly string[] VariantFields = ["sku", "name", "position", "priceVnd", "isUniquePiece", "weightG", "packageDimsCm", "barcode", "lowStockThreshold", "status", "attributes", "rowVersion"];
    private static readonly string[] ImageFields = ["assetId", "variantId", "altText", "sortOrder", "rowVersion"];
    private static Error Missing => new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound);
    private static Error Forbidden => new("forbidden", "Bạn không có quyền thực hiện thao tác này.", ErrorType.Forbidden);
    private static Error Stale => new("concurrency_conflict", "Dữ liệu đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);
    public async Task<Result<ProductData>> MutateAsync(ProductMutation command, CancellationToken ct)
    {
        var lifecycle = command.Operation is "publish" or "archive" or "restore-draft";
        if (!command.Actor.Has(lifecycle ? "catalog.publish" : "catalog.write")) return Fail(Forbidden);
        var create = command.Id is null;
        await store.LockAsync(create || lifecycle || command.Fields.TryGetProperty("slug", out _), ct);
        var p = create ? new ProductData { Id = Guid.CreateVersion7(), CreatedAt = Now() } : await store.LoadAsync(command.Id!.Value, true, ct);
        if (p is null) return Fail(Missing);
        var fields = command.Fields;
        var variant = command.Operation == "variant" && command.ChildId is { } vid ? p.Variants.SingleOrDefault(x => x.Id == vid) : null;
        if (command.Operation == "variant" && command.ChildId is not null && variant is null) return Fail(Missing);
        if (!create)
        {
            var expected = variant?.Version ?? p.Version;
            if (!fields.TryGetProperty("rowVersion", out var rv) || rv.ValueKind != JsonValueKind.String || !CatalogVersions.TryDecode(rv.GetString(), out var version)) return Fail(ProductRules.Bad("rowVersion"));
            if (expected != version) return Fail(Stale);
            if (p.FirstPublishedAt is null && (p.PublishedAt is not null || p.ArchivedRedirectPath is not null || p.Status == "active"))
                return Fail(new("publication_history_unverified", "Cần xác minh lịch sử công khai trước khi sửa.", ErrorType.Conflict));
        }
        var oldType = p.ProductTypeId; var oldNoindex = p.RobotsNoindex;
        var oldValues = p.Attributes.ToArray();
        var oldVariantValues = p.Variants.ToDictionary(x => x.Id, x => x.Attributes.ToArray());
        try
        {
            switch (command.Operation)
            {
                case "product":
                    if (!ProductRules.Apply(p, fields, ProductFields)) return Fail(ProductRules.Bad("fields"));
                    if (fields.TryGetProperty("descriptionHtml", out _) && !fields.TryGetProperty("descriptionJson", out _)) p.DescriptionJson = null;
                    if (create)
                    {
                        p.Variants.Add(new VariantData { Id = Guid.CreateVersion7() });
                        if (fields.TryGetProperty("defaultVariant", out var initial))
                        {
                            if (initial.ValueKind != JsonValueKind.Object) return Fail(ProductRules.Bad("defaultVariant"));
                            var error = await ApplyVariant(p, p.Variants[0], initial, true, command.Actor, ct); if (error is not null) return Fail(error);
                        }
                    }
                    else if (fields.TryGetProperty("defaultVariant", out _)) return Fail(ProductRules.Bad("defaultVariant"));
                    break;
                case "variant":
                    var fresh = variant is null;
                    variant ??= new VariantData { Id = Guid.CreateVersion7(), Position = p.Variants.Count };
                    var variantError = await ApplyVariant(p, variant, fields, fresh, command.Actor, ct); if (variantError is not null) return Fail(variantError);
                    if (fresh) p.Variants.Add(variant);
                    break;
                case "attach-media":
                case "patch-media":
                    var image = command.Operation == "attach-media" ? new ProductImage { Id = Guid.CreateVersion7() } : p.Media.SingleOrDefault(x => x.Id == command.ChildId);
                    if (image is null) return Fail(Missing);
                    if (!ProductRules.Apply(image, fields, ImageFields) || !ValidAlt(image.AltText) || !await store.PublicImageAsync(image.AssetId, ct)
                        || image.VariantId is { } imageVariant && p.Variants.All(x => x.Id != imageVariant)
                        || p.Media.Any(x => x.Id != image.Id && x.AssetId == image.AssetId)) return Fail(ProductRules.Bad("media"));
                    if (command.Operation == "attach-media") p.Media.Add(image);
                    break;
                case "delete-media":
                case "cover":
                    if (!Only(fields, ["rowVersion"])) return Fail(ProductRules.Bad("fields"));
                    var selected = p.Media.SingleOrDefault(x => x.Id == command.ChildId); if (selected is null) return Fail(Missing);
                    if (command.Operation == "delete-media") p.Media.Remove(selected);
                    else { foreach (var item in p.Media) item.IsCover = item.Id == selected.Id; }
                    break;
                case "order":
                    if (!Only(fields, ["rowVersion", "mediaIds"]) || !fields.TryGetProperty("mediaIds", out var order)) return Fail(ProductRules.Bad("mediaIds"));
                    var ids = order.Deserialize<Guid[]>();
                    if (ids is null || ids.Length != p.Media.Count || ids.Distinct().Count() != ids.Length || ids.Any(id => p.Media.All(x => x.Id != id))) return Fail(ProductRules.Bad("mediaIds"));
                    for (var i = 0; i < ids.Length; i++) p.Media.Single(x => x.Id == ids[i]).SortOrder = i;
                    break;
                case "publish":
                case "archive":
                case "restore-draft":
                    if (!Only(fields, command.Operation == "archive" ? ["rowVersion", "redirectTo"] : ["rowVersion"])) return Fail(ProductRules.Bad("fields"));
                    if (command.Operation == "restore-draft" && p.Status != "archived") return Fail(new("invalid_status_transition", "Chỉ khôi phục sản phẩm đã lưu trữ.", ErrorType.Conflict));
                    break;
                default: return Fail(ProductRules.Bad("operation"));
            }
            if (p.Name is not { Length: >= 2 and <= 200 } || p.Name.Any(char.IsControl) || p.CategoryIds is null || p.RelatedProductIds is null || p.Attributes is null
                || p.Variants.Any(x => x.Attributes is null) || p.CategoryIds.Distinct().Count() != p.CategoryIds.Count
                || p.RelatedProductIds.Distinct().Count() != p.RelatedProductIds.Count || p.RelatedProductIds.Contains(p.Id)) return Fail(ProductRules.Bad("product"));
            var reference = await store.ReferenceAsync(p.ProductTypeId, p.MaterialId, ct);
            if (reference is null || !reference.IsActive || !await store.ReferencesExistAsync(p, ct)) return Fail(ProductRules.Bad("references"));
            if (!create && oldType != p.ProductTypeId)
            {
                if (p.Status != "draft") return Fail(new("product_type_locked", "Chỉ đổi loại sản phẩm khi nháp.", ErrorType.Conflict));
                var incompatible = p.Attributes.Any(x => reference.Attributes.All(a => a.Id != x.AttributeId)) || p.Variants.Any(v => v.Attributes.Any(x => reference.Attributes.All(a => a.Id != x.AttributeId)));
                if (incompatible && (!fields.TryGetProperty("confirmTypeChange", out var confirmed) || confirmed.ValueKind != JsonValueKind.True))
                    return Fail(new("product_type_change_confirmation_required", "Đổi loại sẽ xóa giá trị thuộc tính không còn phù hợp. Vui lòng xác nhận.", ErrorType.Conflict));
                p.Attributes.RemoveAll(x => reference.Attributes.All(a => a.Id != x.AttributeId));
                foreach (var v in p.Variants) v.Attributes.RemoveAll(x => reference.Attributes.All(a => a.Id != x.AttributeId));
            }
            // P1-03: deactivated historical assignments remain readable. Validate every new/changed assignment, not unrelated retained values.
            if (DuplicateValues(p.Attributes) || p.Variants.Any(v => DuplicateValues(v.Attributes))
                || ProductRules.Values(ChangedValues(p.Attributes, oldValues), reference, "product").Count > 0
                || p.Variants.Any(v => ProductRules.Values(ChangedValues(v.Attributes, oldVariantValues.GetValueOrDefault(v.Id) ?? []), reference, "variant").Count > 0)) return Fail(ProductRules.Bad("attributes"));
            if (create && !fields.TryGetProperty("slug", out _))
            {
                var basis = TaxonomyRules.Slug(p.Name); var found = false;
                for (var attempt = 1; attempt <= 1000; attempt++) { p.Slug = TaxonomyRules.Candidate(basis, attempt); if (!await store.SlugExistsAsync(p.Slug, p.Id, ct)) { found = true; break; } }
                if (!found) return Fail(new("slug_allocation_exhausted", "Không thể cấp đường dẫn duy nhất.", ErrorType.Conflict));
            }
            if (!TaxonomyRules.ValidSlug(p.Slug)) return Fail(ProductRules.Bad("slug"));
            if (await store.SlugExistsAsync(p.Slug, p.Id, ct)) return Fail(new("slug_exists", "Đường dẫn đã tồn tại.", ErrorType.Conflict));
            var clean = content.Sanitize(p.DescriptionJson, p.DescriptionHtml);
            p.DescriptionJson = clean.Json; p.DescriptionHtml = clean.Html; p.DescriptionWordCount = clean.Words;
            if (p.CareHtml is not null) p.CareHtml = content.Sanitize(null, p.CareHtml).Html;
            if (p.OgMediaId is { } og && !await store.PublicImageAsync(og, ct)) return Fail(ProductRules.Bad("ogMediaId"));
            var reason = fields.TryGetProperty("noindexReason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
            if (p.RobotsNoindex && !oldNoindex && string.IsNullOrWhiteSpace(reason)) return Fail(ProductRules.Bad("noindexReason"));
            if (command.Operation == "publish")
            {
                var cover = p.Media.SingleOrDefault(x => x.IsCover);
                var errors = ProductRules.PublishErrors(p, reference, p.PrimaryCategoryId is { } cat && await store.CategoryVisibleAsync(cat, ct),
                    cover is not null && ValidAlt(cover.AltText) && await store.PublicImageAsync(cover.AssetId, ct));
                if (string.IsNullOrWhiteSpace(clean.Text)) errors["descriptionHtml"] = ["description_missing"];
                if (errors.Count > 0) return Fail(new("publish_requirements_not_met", "Sản phẩm chưa đủ điều kiện công khai.", ErrorType.BusinessRule, errors));
                p.Status = "active"; p.PublishedAt ??= Now(); p.FirstPublishedAt ??= Now(); p.ArchivedRedirectPath = null;
            }
            if (command.Operation == "archive")
            {
                if (fields.TryGetProperty("redirectTo", out var redirectValue) && redirectValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return Fail(ProductRules.Bad("redirectTo"));
                var target = fields.TryGetProperty("redirectTo", out var redirect) && redirect.ValueKind != JsonValueKind.Null ? redirect.GetString() : null;
                p.ArchivedRedirectPath = p.FirstPublishedAt is null ? null : target ?? await store.ArchiveTargetAsync(p, ct);
                if (p.FirstPublishedAt is not null && !ValidPath(p.ArchivedRedirectPath)) return Fail(ProductRules.Bad("redirectTo"));
                p.Status = "archived";
            }
            if (command.Operation == "restore-draft") { p.Status = "draft"; p.ArchivedRedirectPath = null; }
            if (p.Status == "active" && command.Operation != "publish")
            {
                // Parent lock acquired before LoadAsync remains held through validation, save and commit.
                var axisErrors = ProductRules.VariantAxisErrors(p, reference);
                if (axisErrors.Count > 0) return Fail(new("variant_requirements_not_met", "Biến thể chưa đáp ứng điều kiện của sản phẩm công khai.", ErrorType.BusinessRule, axisErrors));
            }
            if (create)
            {
                if (reference.Prefix is null) return Fail(Error.BusinessRule("sku_prefix_missing", "Loại sản phẩm chưa có tiền tố SKU."));
                if (p.MaterialId is not null && reference.MaterialCode is null) return Fail(Error.BusinessRule("material_sku_code_missing", "Chất liệu chưa có mã SKU."));
                p.SkuBase = await store.AllocateBaseAsync(reference, p.MaterialId is not null, ct);
            }
            p.UpdatedAt = Now();
            await store.SaveAsync(p, create, command.Actor, reason, ct);
            return Result.Success((await store.LoadAsync(p.Id, false, ct))!);
        }
        catch (CatalogPersistenceException exception) { return Fail(exception.Error); }
        catch (JsonException) { return Fail(ProductRules.Bad("fields")); }
        catch (ArgumentException) { return Fail(ProductRules.Bad("content")); }
    }
    private async Task<Error?> ApplyVariant(ProductData p, VariantData v, JsonElement fields, bool create, ProductActor actor, CancellationToken ct)
    {
        var oldSku = v.Sku; var oldStatus = v.Status;
        if (!ProductRules.Apply(v, fields, VariantFields)) return ProductRules.Bad("variant");
        if (fields.TryGetProperty("priceVnd", out _) && !actor.Has("pricing.write")) return Forbidden;
        if (fields.TryGetProperty("sku", out _) && v.Sku != oldSku)
        {
            if (!actor.IsOwner) return Forbidden;
            if (!ProductRules.ValidSku(v.Sku)) return ProductRules.Bad("sku");
            if (!create && await store.VariantUsedAsync(v.Id, ct)) return new("sku_locked", "SKU đã có giao dịch, không thể sửa.", ErrorType.Conflict);
        }
        if (v.Status is not ("active" or "inactive") || v.PriceVnd is not null && !ProductRules.ValidPrice(v.PriceVnd) || v.WeightG is <= 0 || v.LowStockThreshold < 0
            || v.Attributes is null) return ProductRules.Bad("variant");
        if (v.Status == "active" && (!create && oldStatus == "inactive" || p.Status == "active") && !ProductRules.ValidPrice(v.PriceVnd))
            return Error.BusinessRule("variant_price_missing", "Biến thể hoạt động cần giá hợp lệ.");
        if (v.PackageDimsCm is not null && TypedAttributeValidator.Validate(new(Guid.Empty, p.ProductTypeId, "dimensions", "variant", true), p.ProductTypeId, "variant", new(ValueJson: v.PackageDimsCm)) is not null)
            return ProductRules.Bad("packageDimsCm");
        return null;
    }
    private DateTime Now() => clock.UtcNow.UtcDateTime;
    private static bool DuplicateValues(List<ProductValue> values) => values.Any(x => x is null) || values.Select(x => x.AttributeId).Distinct().Count() != values.Count;
    private static List<ProductValue> ChangedValues(List<ProductValue> values, IReadOnlyList<ProductValue> original) => values.Where(v => v is null || !original.Any(o =>
        o.AttributeId == v.AttributeId && o.ValueText == v.ValueText && o.ValueNumber == v.ValueNumber && o.OptionId == v.OptionId && o.ValueBool == v.ValueBool
        && (o.ValueJson is null && v.ValueJson is null || o.ValueJson is { } a && v.ValueJson is { } b && JsonElement.DeepEquals(a, b)))).ToList();
    private static bool ValidAlt(string? alt) => alt?.Trim() is { Length: >= 3 and <= 200 };
    private static bool Only(JsonElement fields, string[] keys) => ProductRules.Apply(new object(), fields, keys);
    private static bool ValidPath(string? path) => path is { Length: > 0 and <= 2048 } && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('?') && !path.Contains('#') && !path.Contains('\\') && !path.Any(char.IsControl);
    private static Result<ProductData> Fail(Error error) => Result.Failure<ProductData>(error);
}
public sealed class ProductMutationHandler(ProductWorkflow flow) : ICommandHandler<ProductMutation, ProductData>
{ public Task<Result<ProductData>> Handle(ProductMutation command, CancellationToken cancellationToken) => flow.MutateAsync(command, cancellationToken); }
public sealed class GetProductHandler(IProductStore store) : IQueryHandler<GetProduct, ProductData>
{
    public async Task<Result<ProductData>> Handle(GetProduct query, CancellationToken cancellationToken)
    { var p = await store.LoadAsync(query.Id, false, cancellationToken); return p is null ? Result.Failure<ProductData>(new("not_found", "Không tìm thấy sản phẩm.", ErrorType.NotFound)) : Result.Success(p); }
}
public sealed class ListProductsHandler(IProductStore store) : IQueryHandler<ListProducts, CatalogPage<ProductData>>
{
    public async Task<Result<CatalogPage<ProductData>>> Handle(ListProducts query, CancellationToken cancellationToken)
        => query.Stock == "low" && !query.Actor.Has("inventory.write")
            ? Result.Failure<CatalogPage<ProductData>>(new("forbidden", "Không có quyền đọc mức tồn thấp.", ErrorType.Forbidden))
            : Result.Success(await store.ListAsync(query, cancellationToken));
}
