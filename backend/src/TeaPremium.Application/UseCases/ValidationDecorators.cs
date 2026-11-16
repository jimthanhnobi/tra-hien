using FluentValidation;
using TeaPremium.Application.Results;

namespace TeaPremium.Application.UseCases;

public sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await ValidationErrors.CheckAsync(command, validators, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : Result.Failure<TResult>(error);
    }
}

public sealed class ValidationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await ValidationErrors.CheckAsync(query, validators, cancellationToken);
        return error is null ? await inner.Handle(query, cancellationToken) : Result.Failure<TResult>(error);
    }
}

internal static class ValidationErrors
{
    public static async Task<Error?> CheckAsync<T>(T request, IEnumerable<IValidator<T>> validators, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, ct);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var errors = failures.GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray(), StringComparer.Ordinal);
        return new Error("validation_failed", "Dữ liệu không hợp lệ.", ErrorType.Validation, errors);
    }
}
