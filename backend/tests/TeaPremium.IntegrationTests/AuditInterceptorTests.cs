using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class AuditInterceptorTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TextKeyCreateAuditsSystemActorAndRedactsBankData()
    {
        await using var db = CreateContext();
        var result = await new EfUnitOfWork(db).ExecuteAsync(ct =>
        {
            db.Settings.Add(new Setting
            {
                Key = "payment.bank",
                Value = "{\"bin\":9704,\"account_number\":\"123456789\",\"account_name\":\"Private Person\",\"nested\":{\"notes\":\"call 0901234567\"}}"
            });
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using var reader = fixture.CreateContext();
        var audit = Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == "payment.bank").ToListAsync());
        Assert.Equal("platform.settings", audit.EntityType);
        Assert.Null(audit.EntityId);
        Assert.Equal("create", audit.Action);
        Assert.Equal("system", audit.ActorType);
        Assert.Null(audit.ActorId);
        Assert.Null(audit.CorrelationId);
        Assert.Equal(Instant.UtcDateTime, audit.OccurredAt);
        using var changes = JsonDocument.Parse(audit.Changes);
        Assert.False(changes.RootElement.TryGetProperty("updated_at", out _));
        Assert.NotEqual(default, (await reader.Settings.SingleAsync(x => x.Key == "payment.bank")).UpdatedAt);
        Assert.DoesNotContain("123456789", audit.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Person", audit.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("0901234567", audit.Changes, StringComparison.Ordinal);
        Assert.Contains("[redacted]", audit.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachedUpdateReadsLockedOriginalValuesAndRecordsOnlyChangedColumn()
    {
        const string key = "shipping.flat_fee_vnd";
        await using (var creator = CreateContext())
        {
            await new EfUnitOfWork(creator).ExecuteAsync(ct =>
            {
                creator.Settings.Add(new Setting { Key = key, Value = "30000" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        }

        await using (var writer = CreateContext())
        {
            var detached = new Setting { Key = key, Value = "40000" };
            writer.Settings.Attach(detached);
            writer.Entry(detached).Property(x => x.Value).IsModified = true;
            await new EfUnitOfWork(writer).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);
        }

        await using var reader = fixture.CreateContext();
        var audits = await reader.AuditLogs.Where(x => x.EntityKey == key).OrderBy(x => x.Action).ToListAsync();
        Assert.Equal(2, audits.Count);
        var update = Assert.Single(audits, x => x.Action == "update");
        using var changes = JsonDocument.Parse(update.Changes);
        Assert.Single(changes.RootElement.EnumerateObject());
        Assert.Equal(30000, changes.RootElement.GetProperty("value").GetProperty("before").GetInt32());
        Assert.Equal(40000, changes.RootElement.GetProperty("value").GetProperty("after").GetInt32());
        Assert.Equal("40000", (await reader.Settings.SingleAsync(x => x.Key == key)).Value);
    }

    [Fact]
    public async Task UnchangedSettingDoesNotAddAudit()
    {
        const string key = "orders.max_quantity_per_line_web";
        await using (var creator = CreateContext())
        {
            await new EfUnitOfWork(creator).ExecuteAsync(ct =>
            {
                creator.Settings.Add(new Setting { Key = key, Value = "10" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        }
        await using (var writer = CreateContext())
        {
            var item = await writer.Settings.SingleAsync(x => x.Key == key);
            item.Value = "10";
            await new EfUnitOfWork(writer).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);
        }
        await using var reader = fixture.CreateContext();
        Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == key).ToListAsync());
    }

    [Fact]
    public async Task AttachedStaleEntityAuditsCurrentLockedDatabaseValue()
    {
        const string key = "orders.bank_transfer_hold_hours";
        await using (var creator = CreateContext())
        {
            await new EfUnitOfWork(creator).ExecuteAsync(ct =>
            {
                creator.Settings.Add(new Setting { Key = key, Value = "24" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        }
        await using var stale = CreateContext();
        var detached = new Setting { Key = key, Value = "30" };
        stale.Settings.Attach(detached);
        stale.Entry(detached).Property(x => x.Value).IsModified = true;

        await using (var other = CreateContext())
        {
            var current = await other.Settings.SingleAsync(x => x.Key == key);
            current.Value = "48";
            await new EfUnitOfWork(other).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);
        }
        await new EfUnitOfWork(stale).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);

        await using var reader = fixture.CreateContext();
        var audits = await reader.AuditLogs.Where(x => x.EntityKey == key && x.Action == "update").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, x => JsonDocument.Parse(x.Changes).RootElement.GetProperty("value").GetProperty("before").GetInt32() == 48);
    }

    [Fact]
    public async Task TextKeyDeleteRecordsBeforeAndRemovesBusinessRow()
    {
        const string key = "returns.window_days";
        await using (var creator = CreateContext())
        {
            await new EfUnitOfWork(creator).ExecuteAsync(ct =>
            {
                creator.Settings.Add(new Setting { Key = key, Value = "7" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        }
        await using (var writer = CreateContext())
        {
            var setting = await writer.Settings.SingleAsync(x => x.Key == key);
            writer.Settings.Remove(setting);
            await new EfUnitOfWork(writer).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
        var deletion = Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == key && x.Action == "delete").ToListAsync());
        using var changes = JsonDocument.Parse(deletion.Changes);
        Assert.Equal(7, changes.RootElement.GetProperty("value").GetProperty("before").GetInt32());
        Assert.Equal(JsonValueKind.Null, changes.RootElement.GetProperty("value").GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task PersistedNestedJsonAndArraysAreRedactedOnBothSides()
    {
        const string key = "seo.organization";
        const string before = "{\"has_store\":true,\"address\":{\"phone\":\"0901111111\",\"email\":\"before@example.com\"},\"contacts\":[{\"receiver_name\":\"Before Person\",\"notes\":\"before secret\"}],\"phone0903333333\":\"hidden\"}";
        const string after = "{\"has_store\":false,\"address\":{\"phone\":\"0902222222\",\"email\":\"after@example.com\"},\"contacts\":[{\"receiver_name\":\"After Person\",\"notes\":\"after secret\"}],\"phone0904444444\":\"hidden\"}";
        await using (var creator = CreateContext())
        {
            await new EfUnitOfWork(creator).ExecuteAsync(ct =>
            {
                creator.Settings.Add(new Setting { Key = key, Value = before });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        }
        await using (var writer = CreateContext())
        {
            var setting = await writer.Settings.SingleAsync(x => x.Key == key);
            setting.Value = after;
            await new EfUnitOfWork(writer).ExecuteAsync(ct => Task.FromResult(Result.Success(1)), CancellationToken.None);
        }
        await using var reader = fixture.CreateContext();
        var update = Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == key && x.Action == "update").ToListAsync());
        foreach (var secret in new[] { "0901111111", "0902222222", "0903333333", "0904444444", "before@example.com", "after@example.com", "Before Person", "After Person", "before secret", "after secret" })
            Assert.DoesNotContain(secret, update.Changes, StringComparison.Ordinal);
        using var changes = JsonDocument.Parse(update.Changes);
        var value = changes.RootElement.GetProperty("value");
        Assert.True(value.GetProperty("before").GetProperty("has_store").GetBoolean());
        Assert.False(value.GetProperty("after").GetProperty("has_store").GetBoolean());
        Assert.Equal("[redacted]", value.GetProperty("before").GetProperty("contacts")[0].GetProperty("notes").GetString());
        Assert.Equal("[redacted]", value.GetProperty("after").GetProperty("contacts")[0].GetProperty("notes").GetString());
    }

    [Fact]
    public async Task MultipleSavesCommitBusinessAndAuditTogether()
    {
        const string key = "seo.handling_days";
        await using var db = CreateContext();
        await new EfUnitOfWork(db).ExecuteAsync(async ct =>
        {
            var setting = new Setting { Key = key, Value = "{\"min\":0,\"max\":1}" };
            db.Settings.Add(setting);
            await db.SaveChangesAsync(ct);
            setting.Value = "{\"min\":1,\"max\":2}";
            await db.SaveChangesAsync(ct);
            return Result.Success(1);
        }, CancellationToken.None);

        await using var reader = fixture.CreateContext();
        Assert.Equal(2, await reader.AuditLogs.CountAsync(x => x.EntityKey == key));
        using var value = JsonDocument.Parse((await reader.Settings.SingleAsync(x => x.Key == key)).Value);
        Assert.Equal(1, value.RootElement.GetProperty("min").GetInt32());
        Assert.Equal(2, value.RootElement.GetProperty("max").GetInt32());
    }

    [Theory]
    [InlineData("result")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task FailedCommandRollsBackBusinessAndAudit(string mode)
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        await using var db = CreateContext();
        using var source = new CancellationTokenSource();
        async Task<Result<int>> Operation(CancellationToken ct)
        {
            db.Settings.Add(new Setting { Key = key, Value = "{\"secret\":\"private\"}" });
            await db.SaveChangesAsync(ct);
            if (mode == "exception") throw new InvalidOperationException("test failure");
            if (mode == "cancel")
            {
                source.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Result.Failure<int>(Error.BusinessRule("rejected", "rejected"));
        }

        if (mode == "result")
            Assert.False((await new EfUnitOfWork(db).ExecuteAsync(Operation, source.Token)).IsSuccess);
        else if (mode == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EfUnitOfWork(db).ExecuteAsync(Operation, source.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfUnitOfWork(db).ExecuteAsync(Operation, source.Token));

        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
        Assert.False(await reader.AuditLogs.AnyAsync(x => x.EntityKey == key));
    }

    [Fact]
    public async Task AuditInsertFailurePreventsBusinessCommit()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(db).ExecuteAsync(async ct =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION platform.reject_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit blocked'; END $$; CREATE TRIGGER reject_audit_test BEFORE INSERT ON platform.audit_logs FOR EACH ROW EXECUTE FUNCTION platform.reject_audit_test();", ct);
            db.Settings.Add(new Setting { Key = key, Value = "\"private\"" });
            await db.SaveChangesAsync(ct);
            return Result.Success(1);
        }, CancellationToken.None));
        Assert.IsType<PostgresException>(error.InnerException);
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
        Assert.False(await reader.AuditLogs.AnyAsync(x => x.EntityKey == key));
    }

    [Fact]
    public async Task LaterAuditInsertFailureRollsBackEarlierSavedBusinessAndAudit()
    {
        var first = "test." + Guid.NewGuid().ToString("N");
        var second = "test." + Guid.NewGuid().ToString("N");
        await using var db = CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(db).ExecuteAsync(async ct =>
        {
            db.Settings.Add(new Setting { Key = first, Value = "\"private first\"" });
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION platform.reject_audit_later_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit blocked'; END $$; CREATE TRIGGER reject_audit_later_test BEFORE INSERT ON platform.audit_logs FOR EACH ROW EXECUTE FUNCTION platform.reject_audit_later_test();", ct);
            db.Settings.Add(new Setting { Key = second, Value = "\"private second\"" });
            await db.SaveChangesAsync(ct);
            return Result.Success(1);
        }, CancellationToken.None));
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == first || x.Key == second));
        Assert.False(await reader.AuditLogs.AnyAsync(x => x.EntityKey == first || x.EntityKey == second));
    }

    [Fact]
    public async Task DomainExceptionWithFailedRollbackRemainsInfrastructureFailure()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        var probe = new FailedRollbackProbe();
        await using var db = CreateContext(extra: probe);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfUnitOfWork(db).ExecuteAsync<int>(async ct =>
        {
            db.Settings.Add(new Setting { Key = key, Value = "\"private\"" });
            await db.SaveChangesAsync(ct);
            throw new DomainException("rule_failed", "Private business value");
        }, CancellationToken.None));
        Assert.Equal("Transaction rollback failed; outcome is unknown.", error.Message);
        Assert.IsType<DomainException>(error.InnerException);
        Assert.Same(probe.RollbackError, error.Data["TeaPremium.RollbackFailure"]);
        Assert.Equal(1, probe.Attempts);
    }

    [Fact]
    public async Task DirectAuditLogDoesNotRecursivelyAuditItself()
    {
        await using var db = CreateContext();
        var id = Guid.CreateVersion7();
        db.AuditLogs.Add(new AuditLog
        {
            Id = id,
            OccurredAt = Instant.UtcDateTime,
            ActorType = "system",
            EntityType = "platform.settings",
            EntityKey = "direct.test",
            Action = "access"
        });
        await db.SaveChangesAsync(CancellationToken.None);
        await using var reader = fixture.CreateContext();
        Assert.Single(await reader.AuditLogs.Where(x => x.Id == id).ToListAsync());
    }

    [Fact]
    public void SynchronousSaveRejectsAuditedChange()
    {
        using var db = CreateContext();
        db.Settings.Add(new Setting { Key = "test.sync", Value = "{}" });
        var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Equal("Synchronous SaveChanges is not supported for audited changes.", error.Message);
    }

    [Fact]
    public async Task EmptyTextKeyIsRejectedBeforeDatabaseWrite()
    {
        await using var db = CreateContext();
        db.Settings.Add(new Setting { Key = "", Value = "{}" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Audited entity key is empty or unsupported.", error.Message);
    }

    [Fact]
    public async Task ForgedActorHeaderRemainsSystemAndKeepsMiddlewareCorrelation()
    {
        var context = new DefaultHttpContext();
        var id = Guid.CreateVersion7().ToString("N");
        context.TraceIdentifier = id;
        context.Request.Headers["X-Actor-Type"] = "staff";
        context.Request.Headers["X-Actor-Id"] = Guid.CreateVersion7().ToString();
        var accessor = new HttpContextAccessor { HttpContext = context };
        await using var db = CreateContext(new HttpAuditContext(accessor));
        await new EfUnitOfWork(db).ExecuteAsync(ct =>
        {
            db.Settings.Add(new Setting { Key = "notify.telegram_chat_id", Value = "\"123456789\"" });
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);

        await using var reader = fixture.CreateContext();
        var audit = Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == "notify.telegram_chat_id").ToListAsync());
        Assert.Equal("system", audit.ActorType);
        Assert.Null(audit.ActorId);
        Assert.Equal(id, audit.CorrelationId);
        Assert.DoesNotContain("123456789", audit.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedCookiePrincipalSuppliesActorAndCorrelation()
    {
        var context = new DefaultHttpContext();
        var id = Guid.CreateVersion7().ToString("N");
        var staffId = Guid.CreateVersion7();
        context.TraceIdentifier = id;
        var accessor = new HttpContextAccessor { HttpContext = context };
        var auditContext = new HttpAuditContext(accessor);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, staffId.ToString())], "AdminCookie"));
        await using var db = CreateContext(auditContext);
        await new EfUnitOfWork(db).ExecuteAsync(ct =>
        {
            db.Settings.Add(new Setting { Key = "notify.shop_emails", Value = "[\"secret@example.com\"]" });
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);

        await using var reader = fixture.CreateContext();
        var audit = Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == "notify.shop_emails").ToListAsync());
        Assert.Equal("staff", audit.ActorType);
        Assert.Equal(staffId, audit.ActorId);
        Assert.Equal(id, audit.CorrelationId);
        Assert.DoesNotContain("secret@example.com", audit.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionPersistenceRegistrationInstallsInterceptor()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock());
        services.AddPlatformPersistence(config);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await scope.ServiceProvider.GetRequiredService<TeaPremium.Application.UseCases.IUnitOfWork>()
            .ExecuteAsync(ct =>
            {
                db.Settings.Add(new Setting { Key = key, Value = "\"private\"" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
        await using var reader = fixture.CreateContext();
        Assert.Single(await reader.AuditLogs.Where(x => x.EntityKey == key).ToListAsync());
    }

    private AppDbContext CreateContext(IAuditContext? context = null, IInterceptor? extra = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString);
        options.AddInterceptors(new AuditSaveChangesInterceptor(new FixedClock(), context ?? new SystemAuditContext()));
        if (extra is not null) options.AddInterceptors(extra);
        return new AppDbContext(options.Options);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Instant;
    }

    private sealed class FailedRollbackProbe : DbTransactionInterceptor
    {
        public Exception RollbackError { get; } = new InvalidOperationException("rollback failed");
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            System.Data.Common.DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw RollbackError;
        }
    }
}
