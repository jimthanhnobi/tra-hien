using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Persistence;

public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Result<T>? result = null;
        ExceptionDispatchInfo? failure = null;
        var rollbackAttempted = false;
        var committed = false;
        try
        {
            result = await operation(cancellationToken);
            if (!result.IsSuccess)
            {
                rollbackAttempted = true;
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                committed = true;
            }
        }
        catch (Exception primary)
        {
            failure = ExceptionDispatchInfo.Capture(primary);
            if (!rollbackAttempted)
            {
                rollbackAttempted = true;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackFailure)
                {
                    // A broken connection can leave the server outcome unknown. Keep the primary
                    // exception (including cancellation) and retain the rollback error for diagnosis.
                    if (primary is DomainException)
                    {
                        // The P0-03 command decorator maps DomainException to a normal business
                        // Result. A failed rollback must escape that mapping as infrastructure failure.
                        var transactionFailure = new InvalidOperationException(
                            "Transaction rollback failed; outcome is unknown.", primary);
                        transactionFailure.Data["TeaPremium.RollbackFailure"] = rollbackFailure;
                        failure = ExceptionDispatchInfo.Capture(transactionFailure);
                    }
                    else
                    {
                        primary.Data["TeaPremium.RollbackFailure"] = rollbackFailure;
                    }
                }
            }
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception disposalFailure)
            {
                if (failure is null) failure = ExceptionDispatchInfo.Capture(disposalFailure);
                else failure.SourceException.Data["TeaPremium.TransactionDisposeFailure"] = disposalFailure;
            }
            if (!committed || failure is not null)
            {
                try
                {
                    db.ChangeTracker.Clear();
                }
                catch (Exception trackingFailure)
                {
                    if (failure is null) failure = ExceptionDispatchInfo.Capture(trackingFailure);
                    else failure.SourceException.Data["TeaPremium.ChangeTrackerClearFailure"] = trackingFailure;
                }
            }
        }

        failure?.Throw();
        return result!;
    }
}
