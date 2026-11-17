using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Jobs;

/// <summary>
/// Base for future scheduled jobs. A named job owns one PostgreSQL session advisory lock
/// for its entire execution. Protected work is database-only and uses the supplied
/// context on that same session and transaction. Jobs must not open independent
/// contexts, manage the transaction, or perform external side effects; enqueue outbox
/// messages for future external delivery instead.
/// No business job is registered by P0-06.
/// </summary>
public abstract class PeriodicJob : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogJobFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "PeriodicJobFailure"),
            "Periodic job {JobName} failed; it will retry on the next interval.");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _interval;
    private readonly long _lockKey;

    protected PeriodicJob(string name, TimeSpan interval, IServiceScopeFactory scopeFactory,
        IConfiguration configuration, TimeProvider timeProvider, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Job name must be a stable ASCII identifier.", nameof(name));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Name = name;
        _interval = interval;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Default is required for periodic jobs.");
        // Session locks require a pinned physical connection, never multiplexed commands.
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Multiplexing = false }.ConnectionString;
        _lockKey = AdvisoryLockKey(name);
    }

    public string Name { get; }

    /// <summary>Deterministic across processes and deployments while the job name remains stable.</summary>
    public static long AdvisoryLockKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("TeaPremium.PeriodicJob:" + name));
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    /// <summary>Returns false if another instance owns this job's lock.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            if (!await LockAsync(connection, "SELECT pg_try_advisory_lock(@key)", cancellationToken)) return false;
        }
        catch
        {
            // The server may have acquired the lock before the response was lost.
            NpgsqlConnection.ClearPool(connection);
            throw;
        }

        ExceptionDispatchInfo? workFailure = null;
        try
        {
            await ExecuteProtectedAsync(connection, cancellationToken);
        }
        catch (Exception error)
        {
            workFailure = ExceptionDispatchInfo.Capture(error);
        }

        // Cancellation of the work must not cancel the unlock. Closing a broken
        // physical connection releases the session lock if the unlock fails.
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await LockAsync(connection, "SELECT pg_advisory_unlock(@key)", cleanupTimeout.Token))
                throw new InvalidOperationException("Periodic job advisory lock was not owned at cleanup.");
        }
        catch (Exception cleanupFailure)
        {
            NpgsqlConnection.ClearPool(connection);
            if (workFailure is null)
                throw new InvalidOperationException("Periodic job advisory lock cleanup failed.");
            workFailure.SourceException.Data["TeaPremium.PeriodicJobUnlockFailure"] = cleanupFailure;
        }

        workFailure?.Throw();
        return true;
    }

    /// <summary>
    /// Use only this context for protected work; it already owns the transaction.
    /// Session termination fences its writes and prevents stale-owner commits.
    /// An executing callback may still unwind after lock loss; it has no independent
    /// database write boundary and must not perform external effects.
    /// </summary>
    protected abstract Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken);

    private async Task ExecuteProtectedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        // The owner creates the only transaction before handing a guarded context to work.
        // EF must never replace it or recover by opening a new session/implicit transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var ownership = new JobExecutionGuard(connection, transaction);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection)
            .AddInterceptors(ownership.Interceptors)
            .AddInterceptors(scope.ServiceProvider.GetRequiredService<AuditSaveChangesInterceptor>()).Options;
        await using var db = new AppDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        ownership.Activate(db);
        try
        {
            await ExecuteJobAsync(db, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ownership.Validate();
            await db.SaveChangesAsync(cancellationToken);
            ownership.Validate();
            await transaction.CommitAsync(cancellationToken);
            ownership.Complete();
        }
        catch
        {
            ownership.Revoke();
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(cleanupTimeout.Token);
            }
            catch (Exception)
            {
                // A lost session cannot commit its transaction. Discard this connector;
                // never return a possibly locked session after failed cleanup.
                NpgsqlConnection.ClearPool(connection);
            }
            db.ChangeTracker.Clear();
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A provider or job exception may contain PII or credentials.
                LogJobFailure(_logger, Name, null);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> LockAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("key", NpgsqlDbType.Bigint).Value = _lockKey;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL advisory lock returned no result."));
    }
}
