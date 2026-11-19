using System.Text.Json;
using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Catalog;

// P1-03 / 02 §4: validated queries and transactional commands use the existing pipeline.
public sealed record ListProductTypes(int Page, int PageSize) : IQuery<CatalogPage<ProductTypeView>>;
public sealed record GetProductType(Guid Id) : IQuery<ProductTypeView>;
public sealed record CreateProductType(ProductTypeInput Input) : ICommand<ProductTypeView>;
public sealed record PatchProductType(Guid Id, JsonElement Patch) : ICommand<ProductTypeView>;
public sealed record ListTypeAttributes(Guid TypeId, int Page, int PageSize) : IQuery<CatalogPage<AttributeView>>;
public sealed record GetTypeAttributeQuery(Guid TypeId, Guid Id) : IQuery<AttributeView>;
public sealed record CreateTypeAttributeCommand(Guid TypeId, AttributeInput Input) : ICommand<AttributeView>;
public sealed record PatchTypeAttributeCommand(Guid TypeId, Guid Id, JsonElement Patch) : ICommand<AttributeView>;
public sealed record ListAttributeOptions(Guid TypeId, Guid AttributeId, int Page, int PageSize) : IQuery<CatalogPage<OptionView>>;
public sealed record GetAttributeOption(Guid TypeId, Guid AttributeId, Guid Id) : IQuery<OptionView>;
public sealed record CreateAttributeOption(Guid TypeId, Guid AttributeId, OptionInput Input) : ICommand<OptionView>;
public sealed record PatchAttributeOption(Guid TypeId, Guid AttributeId, Guid Id, JsonElement Patch) : ICommand<OptionView>;
public sealed record SeedCatalogReference : ICommand<CatalogSeedOutcome>;

public sealed class SeedCatalogReferenceHandler(ICatalogReferenceSeed seed) : ICommandHandler<SeedCatalogReference, CatalogSeedOutcome>
{
    public Task<Result<CatalogSeedOutcome>> Handle(SeedCatalogReference command, CancellationToken cancellationToken)
        => seed.SeedAsync(cancellationToken);
}

public sealed class CatalogReferenceHandlers(ICatalogReference catalog) :
    IQueryHandler<ListProductTypes, CatalogPage<ProductTypeView>>,
    IQueryHandler<GetProductType, ProductTypeView>,
    ICommandHandler<CreateProductType, ProductTypeView>,
    ICommandHandler<PatchProductType, ProductTypeView>,
    IQueryHandler<ListTypeAttributes, CatalogPage<AttributeView>>,
    IQueryHandler<GetTypeAttributeQuery, AttributeView>,
    ICommandHandler<CreateTypeAttributeCommand, AttributeView>,
    ICommandHandler<PatchTypeAttributeCommand, AttributeView>,
    IQueryHandler<ListAttributeOptions, CatalogPage<OptionView>>,
    IQueryHandler<GetAttributeOption, OptionView>,
    ICommandHandler<CreateAttributeOption, OptionView>,
    ICommandHandler<PatchAttributeOption, OptionView>
{
    public Task<Result<CatalogPage<ProductTypeView>>> Handle(ListProductTypes query, CancellationToken cancellationToken)
        => catalog.TypesAsync(query.Page, query.PageSize, cancellationToken);
    public Task<Result<ProductTypeView>> Handle(GetProductType query, CancellationToken cancellationToken)
        => catalog.TypeAsync(query.Id, cancellationToken);
    public Task<Result<ProductTypeView>> Handle(CreateProductType command, CancellationToken cancellationToken)
        => catalog.CreateTypeAsync(command.Input, cancellationToken);
    public Task<Result<ProductTypeView>> Handle(PatchProductType command, CancellationToken cancellationToken)
        => catalog.PatchTypeAsync(command.Id, command.Patch, cancellationToken);
    public Task<Result<CatalogPage<AttributeView>>> Handle(ListTypeAttributes query, CancellationToken cancellationToken)
        => catalog.AttributesAsync(query.TypeId, query.Page, query.PageSize, cancellationToken);
    public Task<Result<AttributeView>> Handle(GetTypeAttributeQuery query, CancellationToken cancellationToken)
        => catalog.AttributeAsync(query.TypeId, query.Id, cancellationToken);
    public Task<Result<AttributeView>> Handle(CreateTypeAttributeCommand command, CancellationToken cancellationToken)
        => catalog.CreateAttributeAsync(command.TypeId, command.Input, cancellationToken);
    public Task<Result<AttributeView>> Handle(PatchTypeAttributeCommand command, CancellationToken cancellationToken)
        => catalog.PatchAttributeAsync(command.TypeId, command.Id, command.Patch, cancellationToken);
    public Task<Result<CatalogPage<OptionView>>> Handle(ListAttributeOptions query, CancellationToken cancellationToken)
        => catalog.OptionsAsync(query.TypeId, query.AttributeId, query.Page, query.PageSize, cancellationToken);
    public Task<Result<OptionView>> Handle(GetAttributeOption query, CancellationToken cancellationToken)
        => catalog.OptionAsync(query.TypeId, query.AttributeId, query.Id, cancellationToken);
    public Task<Result<OptionView>> Handle(CreateAttributeOption command, CancellationToken cancellationToken)
        => catalog.CreateOptionAsync(command.TypeId, command.AttributeId, command.Input, cancellationToken);
    public Task<Result<OptionView>> Handle(PatchAttributeOption command, CancellationToken cancellationToken)
        => catalog.PatchOptionAsync(command.TypeId, command.AttributeId, command.Id, command.Patch, cancellationToken);
}

public sealed class ListProductTypesValidator : AbstractValidator<ListProductTypes>
{
    public ListProductTypesValidator()
    {
        RuleFor(x => x.Page).Must((x, page) => CatalogRules.ValidPage(page, x.PageSize)).WithMessage("Phân trang không hợp lệ.");
    }
}
public sealed class GetProductTypeValidator : AbstractValidator<GetProductType>
{
    public GetProductTypeValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
    }
}
public sealed class CreateProductTypeValidator : AbstractValidator<CreateProductType>
{
    public CreateProductTypeValidator()
    {
        RuleFor(x => x.Input).NotNull().WithMessage("Dữ liệu không hợp lệ.").SetValidator(new ProductTypeInputValidator());
    }
}
public sealed class PatchProductTypeValidator : AbstractValidator<PatchProductType>
{
    public PatchProductTypeValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Patch).Must(x => x.ValueKind == JsonValueKind.Object).WithMessage("Dữ liệu không hợp lệ.");
    }
}
public sealed class ListTypeAttributesValidator : AbstractValidator<ListTypeAttributes>
{
    public ListTypeAttributesValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Page).Must((x, page) => CatalogRules.ValidPage(page, x.PageSize)).WithMessage("Phân trang không hợp lệ.");
    }
}
public sealed class GetTypeAttributeQueryValidator : AbstractValidator<GetTypeAttributeQuery>
{
    public GetTypeAttributeQueryValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
    }
}
public sealed class CreateTypeAttributeCommandValidator : AbstractValidator<CreateTypeAttributeCommand>
{
    public CreateTypeAttributeCommandValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Input).NotNull().WithMessage("Dữ liệu không hợp lệ.").SetValidator(new AttributeInputValidator());
    }
}
public sealed class PatchTypeAttributeCommandValidator : AbstractValidator<PatchTypeAttributeCommand>
{
    public PatchTypeAttributeCommandValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Patch).Must(x => x.ValueKind == JsonValueKind.Object).WithMessage("Dữ liệu không hợp lệ.");
    }
}
public sealed class ListAttributeOptionsValidator : AbstractValidator<ListAttributeOptions>
{
    public ListAttributeOptionsValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.AttributeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Page).Must((x, page) => CatalogRules.ValidPage(page, x.PageSize)).WithMessage("Phân trang không hợp lệ.");
    }
}
public sealed class GetAttributeOptionValidator : AbstractValidator<GetAttributeOption>
{
    public GetAttributeOptionValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.AttributeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
    }
}
public sealed class CreateAttributeOptionValidator : AbstractValidator<CreateAttributeOption>
{
    public CreateAttributeOptionValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.AttributeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Input).NotNull().WithMessage("Dữ liệu không hợp lệ.").SetValidator(new OptionInputValidator());
    }
}
public sealed class PatchAttributeOptionValidator : AbstractValidator<PatchAttributeOption>
{
    public PatchAttributeOptionValidator()
    {
        RuleFor(x => x.TypeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.AttributeId).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Id).NotEmpty().WithMessage("Định danh không hợp lệ.");
        RuleFor(x => x.Patch).Must(x => x.ValueKind == JsonValueKind.Object).WithMessage("Dữ liệu không hợp lệ.");
    }
}

