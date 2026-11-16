using TeaPremium.Application.Results;
using TeaPremium.Domain;

namespace TeaPremium.Application.UseCases;

public sealed class TransactionCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteAsync(ct => inner.Handle(command, ct), cancellationToken);
        }
        catch (DomainException exception)
        {
            return Result.Failure<TResult>(Error.BusinessRule(exception.Code, DomainException.PublicMessage));
        }
    }
}
