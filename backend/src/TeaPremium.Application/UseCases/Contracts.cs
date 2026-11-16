using TeaPremium.Application.Results;

namespace TeaPremium.Application.UseCases;

public interface ICommand<TResult>;

public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// P0-04 must implement this port with a real database transaction. An implementation must
/// commit only successful results and roll back failures, exceptions and cancellation.
/// </summary>
public interface IUnitOfWork
{
    Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken);
}
