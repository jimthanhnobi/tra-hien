using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Jobs;

/// <summary>
/// P0-06: a context has one opportunity to use the session/transaction owning its job lock.
/// It cannot regain write capability by reconnecting after an error. This is a guard for
/// supported EF operations, not a sandbox for arbitrary direct Npgsql or other contexts.
/// </summary>
internal sealed class JobExecutionGuard : IDisposable
{
    private const int Created = 0;
    private const int Active = 1;
    private const int Revoked = 2;
    private const int Completed = 3;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly int _backendPid;
    private AppDbContext? _db;
    private int _state;
    private bool _initialBinding;

    public JobExecutionGuard(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        _backendPid = connection.ProcessID;
        _connection.StateChange += StateChanged;
        Interceptors = [new SaveGuard(this), new CommandGuard(this), new ConnectionGuard(this), new TransactionGuard(this)];
    }

    public IReadOnlyList<IInterceptor> Interceptors { get; }

    public void Activate(AppDbContext db)
    {
        _db = db;
        if (!_initialBinding || Interlocked.CompareExchange(ref _state, Active, Created) != Created)
            throw Reject();
        Validate();
    }

    public void Validate()
    {
        if (Volatile.Read(ref _state) != Active || !OriginalSessionIsValid()
            || _db is null || !ReferenceEquals(_db.Database.GetDbConnection(), _connection)
            || !ReferenceEquals(_db.Database.CurrentTransaction?.GetDbTransaction(), _transaction))
            throw Reject();
    }

    public void Complete()
    {
        if (Interlocked.CompareExchange(ref _state, Completed, Active) != Active)
            throw Reject();
    }

    public void Revoke()
    {
        var state = Volatile.Read(ref _state);
        while (state is Created or Active)
        {
            var observed = Interlocked.CompareExchange(ref _state, Revoked, state);
            if (observed == state) break;
            state = observed;
        }
    }

    public void Dispose()
    {
        Revoke();
        _connection.StateChange -= StateChanged;
    }

    private void StateChanged(object? sender, StateChangeEventArgs args)
    {
        if (args.CurrentState != ConnectionState.Open) Revoke();
    }

    private bool OriginalSessionIsValid()
    {
        try
        {
            return _connection.State == ConnectionState.Open
                && ReferenceEquals(_transaction.Connection, _connection)
                && _connection.ProcessID == _backendPid;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private InvalidOperationException Reject()
    {
        Revoke();
        return new InvalidOperationException("Periodic job execution is no longer writable.");
    }

    private void ValidateCommand(DbCommand command, CommandEventData data)
    {
        Validate();
        // Only generated tracked writes and LINQ reads are supported here. Raw SQL can
        // contain transaction control and must not bypass the owner's commit boundary.
        if (data.CommandSource is not (CommandSource.SaveChanges or CommandSource.LinqQuery)
            || !ReferenceEquals(data.Context, _db)
            || !ReferenceEquals(command.Connection, _connection)
            || !ReferenceEquals(command.Transaction, _transaction))
            throw Reject();
    }

    private DbTransaction BindOriginal(DbConnection connection, DbTransaction transaction)
    {
        if (Volatile.Read(ref _state) != Created || _initialBinding || !OriginalSessionIsValid()
            || !ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction))
            throw Reject();
        _initialBinding = true;
        return transaction;
    }

    private sealed class SaveGuard(JobExecutionGuard owner) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            owner.Validate();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            owner.Validate();
            return ValueTask.FromResult(result);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData) => owner.Revoke();
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.Revoke();
            return Task.CompletedTask;
        }

        public override void SaveChangesCanceled(DbContextEventData eventData) => owner.Revoke();
        public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.Revoke();
            return Task.CompletedTask;
        }
    }

    private sealed class CommandGuard(JobExecutionGuard owner) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            owner.ValidateCommand(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            owner.ValidateCommand(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            owner.ValidateCommand(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            owner.ValidateCommand(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            owner.ValidateCommand(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            owner.ValidateCommand(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => owner.Revoke();
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            owner.Revoke();
            return Task.CompletedTask;
        }

        public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => owner.Revoke();
        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            owner.Revoke();
            return Task.CompletedTask;
        }
    }

    private sealed class ConnectionGuard(JobExecutionGuard owner) : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result) => throw owner.Reject();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw owner.Reject();

        public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => owner.Revoke();
        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            owner.Revoke();
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionGuard(JobExecutionGuard owner) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) => throw owner.Reject();

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default) => throw owner.Reject();

        public override DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData,
            DbTransaction result) => owner.BindOriginal(connection, result);

        public override ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(owner.BindOriginal(connection, result));

        public override InterceptionResult TransactionCommitting(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result) => throw owner.Reject();

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw owner.Reject();

        public override InterceptionResult TransactionRollingBack(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result) => throw owner.Reject();

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw owner.Reject();
    }
}
