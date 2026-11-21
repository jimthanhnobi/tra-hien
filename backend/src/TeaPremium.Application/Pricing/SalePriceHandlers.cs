using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Pricing;

public sealed class ListSalePricesHandler(ISalePriceStore store) : IQueryHandler<ListSalePrices, SalePriceList>
{
    public async Task<Result<SalePriceList>> Handle(ListSalePrices query, CancellationToken cancellationToken)
    {
        var prices = await store.ListAsync(query.VariantId, cancellationToken);
        return prices is null ? Result.Failure<SalePriceList>(SalePriceErrors.NotFound) : Result.Success(prices);
    }
}

public sealed class CreateSalePriceHandler(ISalePriceStore store, IClock clock) : ICommandHandler<CreateSalePrice, SalePriceView>
{
    public async Task<Result<SalePriceView>> Handle(CreateSalePrice command, CancellationToken cancellationToken)
    {
        if (!command.Actor.Has("pricing.write")) return Result.Failure<SalePriceView>(SalePriceErrors.Forbidden);
        var variant = await store.LockVariantAsync(command.VariantId, cancellationToken);
        if (variant is null) return Result.Failure<SalePriceView>(SalePriceErrors.NotFound);
        if (!CatalogVersions.TryDecode(command.RowVersion, out var expected)) return Result.Failure<SalePriceView>(ProductRules.Bad("rowVersion"));
        if (variant.Version != expected) return Result.Failure<SalePriceView>(SalePriceErrors.Stale);

        // BR-PRC-02 and BR-CAT-15 use the same eligibility as price evaluation.
        if (!variant.Active || variant.BasePriceVnd is not { } basePrice)
            return Result.Failure<SalePriceView>(Error.BusinessRule("variant_not_sellable", "Biến thể phải hoạt động và có giá niêm yết."));
        if (command.SalePriceVnd >= basePrice)
            return Result.Failure<SalePriceView>(ProductRules.Bad("salePriceVnd"));

        return await store.AddAsync(variant, command, SalePriceTime.ToPersistedUtc(clock.UtcNow), cancellationToken);
    }
}

public sealed class DeleteSalePriceHandler(ISalePriceStore store) : ICommandHandler<DeleteSalePrice, bool>
{
    public async Task<Result<bool>> Handle(DeleteSalePrice command, CancellationToken cancellationToken)
    {
        if (!command.Actor.Has("pricing.write")) return Result.Failure<bool>(SalePriceErrors.Forbidden);
        var variant = await store.LockVariantAsync(command.VariantId, cancellationToken);
        if (variant is null) return Result.Failure<bool>(SalePriceErrors.NotFound);
        if (!SalePriceVersion.TryDecode(command.RowVersion, out var version)) return Result.Failure<bool>(ProductRules.Bad("rowVersion"));
        return await store.DeleteAsync(variant, command, version, cancellationToken);
    }
}

public static class SalePriceErrors
{
    public static Error NotFound => new("not_found", "Không tìm thấy biến thể hoặc giá khuyến mãi.", ErrorType.NotFound);
    public static Error Forbidden => new("forbidden", "Bạn không có quyền thực hiện thao tác này.", ErrorType.Forbidden);
    public static Error Stale => new("concurrency_conflict", "Dữ liệu đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);
}
