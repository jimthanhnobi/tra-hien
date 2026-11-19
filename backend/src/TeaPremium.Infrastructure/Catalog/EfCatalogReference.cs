using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed class EfCatalogReference(AppDbContext db, IUnitOfWork unitOfWork, IClock clock, IOutboxWriter outbox) : ICatalogReference
{
    public async Task<Result<CatalogPage<ProductTypeView>>> TypesAsync(int page, int pageSize, CancellationToken ct)
    {
        if (!CatalogRules.ValidPage(page, pageSize)) return Fail<CatalogPage<ProductTypeView>>(CatalogRules.Invalid());
        var query = db.Set<ProductType>().AsNoTracking();
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return Result.Success(new CatalogPage<ProductTypeView>(items.Select(View).ToArray(), page, pageSize, total));
    }
    public async Task<Result<ProductTypeView>> TypeAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<ProductType>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return entity is null ? Fail<ProductTypeView>(CatalogRules.Missing) : Result.Success(View(entity));
    }
    public Task<Result<ProductTypeView>> CreateTypeAsync(ProductTypeInput input, CancellationToken ct) => Write(token =>
    {
        if (CatalogRules.Validate(input, new ProductTypeInputValidator()) is { } error) return Task.FromResult(Fail<ProductTypeView>(error));
        var entity = new ProductType { Id = Guid.CreateVersion7(), CreatedAt = Now() };
        Apply(entity, input); db.Add(entity);
        return Task.FromResult(Result.Success(View(entity)));
    }, ct);
    public Task<Result<ProductTypeView>> PatchTypeAsync(Guid id, JsonElement patch, CancellationToken ct) => Write(async token =>
    {
        var entity = await db.Set<ProductType>().SingleOrDefaultAsync(x => x.Id == id, token);
        if (entity is null) return Fail<ProductTypeView>(CatalogRules.Missing);
        var next = CatalogRules.Patch(Input(entity), View(entity).RowVersion, patch);
        if (!next.IsSuccess) return Fail<ProductTypeView>(next.Error);
        if (CatalogRules.Validate(next.Value, new ProductTypeInputValidator()) is { } error) return Fail<ProductTypeView>(error);
        var renamed = entity.Name != next.Value.Name;
        Apply(entity, next.Value);
        if (renamed)
        {
            // BR-CAT-10. Emit explicit, bounded IDs in the same UoW; handler reads current names.
            var ids = await db.Set<Product>().Where(x => x.ProductTypeId == id).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token);
            foreach (var batch in ids.Chunk(200)) outbox.Enqueue(RebuildProductSearchText.Message, JsonSerializer.Serialize(new RebuildProductSearchText(batch)));
        }
        return Result.Success(View(entity));
    }, ct);

    public async Task<Result<CatalogPage<AttributeView>>> AttributesAsync(Guid typeId, int page, int pageSize, CancellationToken ct)
    {
        if (!CatalogRules.ValidPage(page, pageSize)) return Fail<CatalogPage<AttributeView>>(CatalogRules.Invalid());
        if (!await db.Set<ProductType>().AnyAsync(x => x.Id == typeId, ct)) return Fail<CatalogPage<AttributeView>>(CatalogRules.Missing);
        var query = db.Set<AttributeDefinition>().AsNoTracking().Where(x => x.ProductTypeId == typeId);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return Result.Success(new CatalogPage<AttributeView>(items.Select(View).ToArray(), page, pageSize, total));
    }
    public async Task<Result<AttributeView>> AttributeAsync(Guid typeId, Guid id, CancellationToken ct)
    {
        var entity = await db.Set<AttributeDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.ProductTypeId == typeId && x.Id == id, ct);
        return entity is null ? Fail<AttributeView>(CatalogRules.Missing) : Result.Success(View(entity));
    }
    public Task<Result<AttributeView>> CreateAttributeAsync(Guid typeId, AttributeInput input, CancellationToken ct) => Write(async token =>
    {
        if (!await db.Set<ProductType>().AnyAsync(x => x.Id == typeId, token)) return Fail<AttributeView>(CatalogRules.Missing);
        if (CatalogRules.Validate(input, new AttributeInputValidator()) is { } error) return Fail<AttributeView>(error);
        var entity = new AttributeDefinition { Id = Guid.CreateVersion7(), ProductTypeId = typeId };
        Apply(entity, input); db.Add(entity);
        return Result.Success(View(entity));
    }, ct);
    public Task<Result<AttributeView>> PatchAttributeAsync(Guid typeId, Guid id, JsonElement patch, CancellationToken ct) => Write(async token =>
    {
        var entity = await db.Set<AttributeDefinition>().SingleOrDefaultAsync(x => x.ProductTypeId == typeId && x.Id == id, token);
        if (entity is null) return Fail<AttributeView>(CatalogRules.Missing);
        var current = Input(entity);
        var next = CatalogRules.Patch(current, View(entity).RowVersion, patch);
        if (!next.IsSuccess) return Fail<AttributeView>(next.Error);
        if (CatalogRules.Validate(next.Value, new AttributeInputValidator()) is { } error) return Fail<AttributeView>(error);
        var hasValues = await db.Set<ProductAttributeValue>().AnyAsync(x => x.AttributeId == id, token)
            || await db.Set<VariantAttributeValue>().AnyAsync(x => x.AttributeId == id, token);
        var hasOptions = await db.Set<AttributeOption>().AnyAsync(x => x.AttributeId == id, token);
        if (CatalogRules.SchemaChange(current, next.Value, hasValues, hasOptions) is { } changeError)
            return Fail<AttributeView>(changeError);
        Apply(entity, next.Value);
        return Result.Success(View(entity));
    }, ct);

    private Task<bool> OwnsAttribute(Guid typeId, Guid attributeId, CancellationToken ct)
        => db.Set<AttributeDefinition>().AnyAsync(x => x.Id == attributeId && x.ProductTypeId == typeId, ct);
    public async Task<Result<CatalogPage<OptionView>>> OptionsAsync(Guid typeId, Guid attributeId, int page, int pageSize, CancellationToken ct)
    {
        if (!CatalogRules.ValidPage(page, pageSize)) return Fail<CatalogPage<OptionView>>(CatalogRules.Invalid());
        if (!await OwnsAttribute(typeId, attributeId, ct)) return Fail<CatalogPage<OptionView>>(CatalogRules.Missing);
        var query = db.Set<AttributeOption>().AsNoTracking().Where(x => x.AttributeId == attributeId);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return Result.Success(new CatalogPage<OptionView>(items.Select(View).ToArray(), page, pageSize, total));
    }
    public async Task<Result<OptionView>> OptionAsync(Guid typeId, Guid attributeId, Guid id, CancellationToken ct)
    {
        if (!await OwnsAttribute(typeId, attributeId, ct)) return Fail<OptionView>(CatalogRules.Missing);
        var entity = await db.Set<AttributeOption>().AsNoTracking().SingleOrDefaultAsync(x => x.AttributeId == attributeId && x.Id == id, ct);
        return entity is null ? Fail<OptionView>(CatalogRules.Missing) : Result.Success(View(entity));
    }
    public Task<Result<OptionView>> CreateOptionAsync(Guid typeId, Guid attributeId, OptionInput input, CancellationToken ct) => Write(async token =>
    {
        var parent = await db.Set<AttributeDefinition>().SingleOrDefaultAsync(x => x.Id == attributeId && x.ProductTypeId == typeId, token);
        if (parent is null) return Fail<OptionView>(CatalogRules.Missing);
        if (CatalogRules.OptionAttribute(parent.DataType) is { } typeError) return Fail<OptionView>(typeError);
        if (CatalogRules.Validate(input, new OptionInputValidator()) is { } error) return Fail<OptionView>(error);
        var entity = new AttributeOption { Id = Guid.CreateVersion7(), AttributeId = attributeId };
        Apply(entity, input); db.Add(entity);
        return Result.Success(View(entity));
    }, ct);
    public Task<Result<OptionView>> PatchOptionAsync(Guid typeId, Guid attributeId, Guid id, JsonElement patch, CancellationToken ct) => Write(async token =>
    {
        if (!await OwnsAttribute(typeId, attributeId, token)) return Fail<OptionView>(CatalogRules.Missing);
        var entity = await db.Set<AttributeOption>().SingleOrDefaultAsync(x => x.AttributeId == attributeId && x.Id == id, token);
        if (entity is null) return Fail<OptionView>(CatalogRules.Missing);
        var next = CatalogRules.Patch(Input(entity), View(entity).RowVersion, patch);
        if (!next.IsSuccess) return Fail<OptionView>(next.Error);
        if (CatalogRules.Validate(next.Value, new OptionInputValidator()) is { } error) return Fail<OptionView>(error);
        // Code/label/activity do not change the option's stable FK identity. Inactive values
        // remain readable; TypedAttributeValidator disallows them for new assignments.
        Apply(entity, next.Value);
        return Result.Success(View(entity));
    }, ct);

    private async Task<Result<T>> Write<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken ct)
    {
        try
        {
            async Task<Result<T>> Execute(CancellationToken token)
            {
                await LockAsync(db, token);
                var result = await operation(token);
                // Flush inside the adapter so expected provider conflicts are translated before
                // the command decorator chooses commit/rollback. Audit/outbox share this transaction.
                if (result.IsSuccess) await db.SaveChangesAsync(token);
                return result;
            }
            return db.Database.CurrentTransaction is not null
                ? await Execute(ct)
                : await unitOfWork.ExecuteAsync(Execute, ct);
        }
        catch (DbUpdateException exception) when (UniqueError(exception) is not null)
        { return Fail<T>(CatalogRules.Conflict(UniqueError(exception)!)); }
    }
    internal static string? UniqueError(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        ? pg.ConstraintName switch
        {
            "ux_product_types_code" => "product_type_code_exists",
            "ux_attribute_definitions_product_type_id_code" => "attribute_code_exists",
            "ux_attribute_options_attribute_id_code" => "option_code_exists",
            _ => null
        } : null;

    internal static Task LockAsync(AppDbContext db, CancellationToken ct)
        // Small reference catalog: serialize metadata/seed writes; block concurrent value
        // inserts while checking incompatible mutations. No lock or change to stock/SKUs.
        => db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE catalog.product_types, catalog.attribute_definitions, catalog.attribute_options,
                catalog.materials IN SHARE ROW EXCLUSIVE MODE;
            LOCK TABLE catalog.product_attribute_values, catalog.variant_attribute_values IN SHARE MODE;
            """, ct);

    private void Apply(ProductType e, ProductTypeInput x)
    { e.Code = x.Code; e.Name = x.Name; e.SkuPrefix = x.SkuPrefix; e.Description = x.Description; e.GoogleProductCategory = x.GoogleProductCategory; e.SortOrder = x.SortOrder; e.IsActive = x.IsActive; e.UpdatedAt = Now(); }
    private DateTime Now()
    {
        var now = clock.UtcNow.UtcDateTime;
        return now.AddTicks(-(now.Ticks % 10)); // PostgreSQL timestamptz has microsecond precision.
    }
    private static void Apply(AttributeDefinition e, AttributeInput x)
    { e.Code = x.Code; e.Name = x.Name; e.DataType = x.DataType; e.Unit = x.Unit; e.Scope = x.Scope; e.IsVariantAxis = x.IsVariantAxis; e.IsRequiredForPublish = x.IsRequiredForPublish; e.IsFilterable = x.IsFilterable; e.IsVisible = x.IsVisible; e.SortOrder = x.SortOrder; e.IsActive = x.IsActive; }
    private static void Apply(AttributeOption e, OptionInput x)
    { e.Code = x.Code; e.Label = x.Label; e.SortOrder = x.SortOrder; e.IsActive = x.IsActive; }
    internal static ProductTypeInput Input(ProductType x) => new(x.Code, x.Name, x.SkuPrefix, x.Description, x.GoogleProductCategory, x.SortOrder, x.IsActive);
    internal static AttributeInput Input(AttributeDefinition x) => new(x.Code, x.Name, x.DataType, x.Unit, x.Scope, x.IsVariantAxis, x.IsRequiredForPublish, x.IsFilterable, x.IsVisible, x.SortOrder, x.IsActive);
    internal static OptionInput Input(AttributeOption x) => new(x.Code, x.Label, x.SortOrder, x.IsActive);
    internal static ProductTypeView View(ProductType x) => new(x.Id, x.Code, x.SkuPrefix, x.Name, x.Description, x.GoogleProductCategory, x.SortOrder, x.IsActive, x.CreatedAt, x.UpdatedAt, CatalogRules.Version(new { x.Id, Metadata = Input(x) }));
    internal static AttributeView View(AttributeDefinition x) => new(x.Id, x.ProductTypeId, x.Code, x.Name, x.DataType, x.Unit, x.Scope, x.IsVariantAxis, x.IsRequiredForPublish, x.IsFilterable, x.IsVisible, x.SortOrder, x.IsActive, CatalogRules.Version(new { x.Id, x.ProductTypeId, Metadata = Input(x) }));
    internal static OptionView View(AttributeOption x) => new(x.Id, x.AttributeId, x.Code, x.Label, x.SortOrder, x.IsActive, CatalogRules.Version(new { x.Id, x.AttributeId, Metadata = Input(x) }));
    private static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);
}
