using System.Text.Json.Serialization;
using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;

namespace TeaPremium.Application.Inventory;

public sealed record InventoryStockView(Guid VariantId, Guid ProductId, Guid ProductTypeId, string ProductName,
    string? VariantName, string Sku, string Availability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OnHand = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Reserved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Available = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LowStock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AvgCostVnd = null);
public sealed record InventoryMovementView(Guid Id, string MovementType, int Quantity, int OnHandAfter,
    string? RefType, Guid? RefId, string? Reason, string ActorType, Guid? ActorId, DateTime OccurredAt, DateTime CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UnitCostVnd = null);

public sealed record ListInventory(int Page, int PageSize, string? Q, string? Status, Guid? Type, bool Detailed, bool Costs)
    : IQuery<CatalogPage<InventoryStockView>>;
public sealed record ListMovements(Guid VariantId, int Page, int PageSize, string? Type, DateTime? From, DateTime? To, bool Costs)
    : IQuery<CatalogPage<InventoryMovementView>>;

public interface IInventoryQueries
{
    Task<CatalogPage<InventoryStockView>> ListAsync(ListInventory query, CancellationToken ct);
    Task<CatalogPage<InventoryMovementView>?> MovementsAsync(ListMovements query, CancellationToken ct);
}

public sealed class ListInventoryValidator : AbstractValidator<ListInventory>
{
    public ListInventoryValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Q).MaximumLength(200).Must(x => x?.Any(char.IsControl) != true);
        RuleFor(x => x.Status).Must(x => x is null or "in" or "out" or "low");
        RuleFor(x => x.Type).Must(x => x != Guid.Empty);
    }
}

public sealed class ListMovementsValidator : AbstractValidator<ListMovements>
{
    public ListMovementsValidator()
    {
        RuleFor(x => x.VariantId).NotEmpty();
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Type).Must(x => x is null || StockRules.IsMovementType(x));
        RuleFor(x => x.To).Must((q, to) => to is null || q.From is null || q.From < to);
    }
}

public sealed class ListInventoryHandler(IInventoryQueries queries) : IQueryHandler<ListInventory, CatalogPage<InventoryStockView>>
{
    public async Task<Result<CatalogPage<InventoryStockView>>> Handle(ListInventory query, CancellationToken cancellationToken) =>
        !query.Detailed && query.Status == "low"
            ? Result.Failure<CatalogPage<InventoryStockView>>(new("forbidden", "Không có quyền đọc mức tồn thấp.", ErrorType.Forbidden))
            : Result.Success(await queries.ListAsync(query, cancellationToken));
}

public sealed class ListMovementsHandler(IInventoryQueries queries) : IQueryHandler<ListMovements, CatalogPage<InventoryMovementView>>
{
    public async Task<Result<CatalogPage<InventoryMovementView>>> Handle(ListMovements query, CancellationToken cancellationToken)
    {
        var page = await queries.MovementsAsync(query, cancellationToken);
        return page is null
            ? Result.Failure<CatalogPage<InventoryMovementView>>(new("not_found", "Không tìm thấy biến thể.", ErrorType.NotFound))
            : Result.Success(page);
    }
}
