using System.Data.Common;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace TeaPremium.IntegrationTests;

public sealed class PlatformDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    public string ConnectionString => _container.GetConnectionString();

    public AppDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options);
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAsync("CREATE ROLE tp_app LOGIN PASSWORD 'Disposable-Test-Only!'; CREATE ROLE tp_readonly NOLOGIN; CREATE ROLE tp_owner NOLOGIN");
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class PlatformDatabaseTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task MigrationCreatesExactPlatformShape()
    {
        var tables = Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'platform' AND table_type = 'BASE TABLE'"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(10, tables);
        Assert.Equal("administrative_data_imports,audit_logs,data_protection_keys,idempotency_keys,inbound_webhooks,notification_logs,outbox_messages,provinces,settings,wards", await fixture.ScalarAsync("SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema = 'platform' AND table_type = 'BASE TABLE'"));
        Assert.Equal(30, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'platform'"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(20, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'platform'"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(8, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'platform' AND indexname LIKE 'ix_%'"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(12, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'platform' AND indexname NOT LIKE 'ix_%'"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM pg_extension WHERE extname IN ('unaccent','pg_trgm','btree_gist')"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("dang", await fixture.ScalarAsync("SELECT public.f_unaccent('Đặng')"));
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'platform' AND column_name = 'xmin'"), System.Globalization.CultureInfo.InvariantCulture));

        const string expected = "ck_audit_entity_identity,ck_audit_logs_action,ck_audit_logs_actor_type,ck_inbound_webhooks_status,ck_notification_logs_channel,ck_notification_logs_status,ck_outbox_messages_status,ck_settings_key_format,fk_wards_provinces_province_code,pk_audit_logs,pk_data_protection_keys,pk_idempotency_keys,pk_inbound_webhooks,pk_notification_logs,pk_outbox_messages,pk_provinces,pk_settings,pk_wards,ux_inbound_webhooks_provider_external_id";
        Assert.Equal(expected, await fixture.ScalarAsync("SELECT string_agg(c.conname, ',' ORDER BY c.conname) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'platform' AND c.conrelid <> 'platform.administrative_data_imports'::regclass"));
        Assert.Equal("ix_audit_actor,ix_audit_entity_id,ix_audit_entity_key,ix_audit_time,ix_idem_created,ix_notif_ref,ix_outbox_pending,ix_wards_province", await fixture.ScalarAsync("SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname='platform' AND indexname LIKE 'ix_%'"));
        Assert.Equal("scope, key", await fixture.ScalarAsync("SELECT string_agg(a.attname, ', ' ORDER BY u.ord) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS u(attnum, ord) JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = u.attnum WHERE n.nspname='platform' AND t.relname='idempotency_keys' AND c.contype='p'"));
        Assert.Equal("provider, external_id", await fixture.ScalarAsync("SELECT string_agg(a.attname, ', ' ORDER BY u.ord) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS u(attnum, ord) JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = u.attnum WHERE n.nspname='platform' AND t.relname='inbound_webhooks' AND c.contype='u'"));
        Assert.Equal("NO ACTION", await fixture.ScalarAsync("SELECT CASE c.confdeltype WHEN 'a' THEN 'NO ACTION' ELSE 'OTHER' END FROM pg_constraint c WHERE c.conname='fk_wards_provinces_province_code'"));
        var uuidIndex = (string)(await fixture.ScalarAsync("SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ix_audit_entity_id'") ?? "");
        Assert.Contains("(entity_type, entity_id, occurred_at DESC)", uuidIndex, StringComparison.Ordinal);
        Assert.Contains("WHERE (entity_id IS NOT NULL)", uuidIndex, StringComparison.Ordinal);
        var textIndex = (string)(await fixture.ScalarAsync("SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ix_audit_entity_key'") ?? "");
        Assert.Contains("(entity_type, entity_key, occurred_at DESC)", textIndex, StringComparison.Ordinal);
        Assert.Contains("WHERE (entity_key IS NOT NULL)", textIndex, StringComparison.Ordinal);
        Assert.Contains("(occurred_at DESC)", (string)(await fixture.ScalarAsync("SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ix_audit_time'") ?? ""), StringComparison.Ordinal);
        Assert.Contains("(actor_id, occurred_at DESC) WHERE (actor_id IS NOT NULL)", (string)(await fixture.ScalarAsync("SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ix_audit_actor'") ?? ""), StringComparison.Ordinal);
        Assert.Contains("(available_at) WHERE (status = 'pending'::text)", (string)(await fixture.ScalarAsync("SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ix_outbox_pending'") ?? ""), StringComparison.Ordinal);
        var identityCheck = (string)(await fixture.ScalarAsync("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='ck_audit_entity_identity'") ?? "");
        Assert.Contains("num_nonnulls(entity_id, entity_key) = 1", identityCheck, StringComparison.Ordinal);
        Assert.Contains("entity_key <> ''::text", identityCheck, StringComparison.Ordinal);
        Assert.Equal("nextval('platform.data_protection_keys_id_seq'::regclass)", await fixture.ScalarAsync("SELECT pg_get_expr(d.adbin,d.adrelid) FROM pg_attrdef d JOIN pg_class t ON t.oid=d.adrelid JOIN pg_namespace n ON n.oid=t.relnamespace JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=d.adnum WHERE n.nspname='platform' AND t.relname='data_protection_keys' AND a.attname='id'"));
    }

    [Theory]
    [InlineData("INSERT INTO platform.settings(key,value) VALUES ('BAD','{}')", "23514")]
    [InlineData("INSERT INTO platform.wards(code,province_code,name) VALUES ('w_bad','none','Ward')", "23503")]
    [InlineData("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,action) VALUES ('00000000-0000-0000-0000-000000000001',now(),'staff','platform.settings','create')", "23514")]
    [InlineData("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_id,entity_key,action) VALUES ('00000000-0000-0000-0000-000000000002',now(),'staff','platform.settings','00000000-0000-0000-0000-000000000001','key','create')", "23514")]
    [InlineData("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action) VALUES ('00000000-0000-0000-0000-000000000003',now(),'staff','platform.settings','','create')", "23514")]
    [InlineData("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action) VALUES ('00000000-0000-0000-0000-000000000004',now(),'other','platform.settings','key','create')", "23514")]
    [InlineData("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action) VALUES ('00000000-0000-0000-0000-000000000005',now(),'staff','platform.settings','key','other')", "23514")]
    [InlineData("INSERT INTO platform.outbox_messages(id,type,payload,occurred_at,available_at,status) VALUES ('00000000-0000-0000-0000-000000000006','x','{}',now(),now(),'other')", "23514")]
    [InlineData("INSERT INTO platform.inbound_webhooks(id,provider,external_id,payload,status) VALUES ('00000000-0000-0000-0000-000000000007','x','x','{}','other')", "23514")]
    [InlineData("INSERT INTO platform.notification_logs(id,channel,template,recipient_hash,status) VALUES ('00000000-0000-0000-0000-000000000008','other','x','hash','queued')", "23514")]
    [InlineData("INSERT INTO platform.notification_logs(id,channel,template,recipient_hash,status) VALUES ('00000000-0000-0000-0000-000000000009','email','x','hash','other')", "23514")]
    public async Task ConstraintsRejectInvalidRows(string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
    }

    [Fact]
    public async Task ValidIdentitiesCompositeKeyAndSerialWork()
    {
        await using var db = fixture.CreateContext();
        await fixture.ExecuteAsync("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_id,action) VALUES ('00000000-0000-0000-0000-000000000010',now(),'staff','platform.outbox_messages','00000000-0000-0000-0000-000000000010','create')");
        await fixture.ExecuteAsync("INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action) VALUES ('00000000-0000-0000-0000-000000000011',now(),'staff','platform.settings','key','create')");
        await fixture.ExecuteAsync("INSERT INTO platform.idempotency_keys(scope,key,request_hash) VALUES ('a','00000000-0000-0000-0000-000000000012','hash'),('b','00000000-0000-0000-0000-000000000012','hash')");
        var key = new DataProtectionKey { FriendlyName = "test" };
        db.DataProtectionKeys.Add(key);
        await db.SaveChangesAsync();
        Assert.True(key.Id > 0);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("INSERT INTO platform.idempotency_keys(scope,key,request_hash) VALUES ('a','00000000-0000-0000-0000-000000000012','hash')"));
        Assert.Equal("23505", duplicate.SqlState);
    }

    [Fact]
    public async Task AuditQueriesMatchSchemaQualifiedTypeAndCorrectIdentityColumn()
    {
        var entityId = Guid.NewGuid();
        var entityKey = "audit." + Guid.NewGuid().ToString("N");
        var uuidLogId = Guid.NewGuid();
        var textLogId = Guid.NewGuid();
        await using (var writer = fixture.CreateContext())
        {
            var occurredAt = DateTime.UtcNow;
            writer.AuditLogs.AddRange(
                new AuditLog { Id = uuidLogId, OccurredAt = occurredAt, ActorType = "system", EntityType = "platform.outbox_messages", EntityId = entityId, Action = "create" },
                new AuditLog { Id = textLogId, OccurredAt = occurredAt, ActorType = "system", EntityType = "platform.settings", EntityKey = entityKey, Action = "update" },
                new AuditLog { Id = Guid.NewGuid(), OccurredAt = occurredAt, ActorType = "system", EntityType = "platform.audit_logs", EntityId = entityId, Action = "create" },
                new AuditLog { Id = Guid.NewGuid(), OccurredAt = occurredAt, ActorType = "system", EntityType = "platform.provinces", EntityKey = entityKey, Action = "update" });
            await writer.SaveChangesAsync();
        }

        await using var reader = fixture.CreateContext();
        var uuidMatches = await reader.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "platform.outbox_messages" && x.EntityId == entityId).ToListAsync();
        var uuidMatch = Assert.Single(uuidMatches);
        Assert.Equal(uuidLogId, uuidMatch.Id);
        Assert.Equal(entityId, uuidMatch.EntityId);
        Assert.Null(uuidMatch.EntityKey);

        var textMatches = await reader.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "platform.settings" && x.EntityKey == entityKey).ToListAsync();
        var textMatch = Assert.Single(textMatches);
        Assert.Equal(textLogId, textMatch.Id);
        Assert.Equal(entityKey, textMatch.EntityKey);
        Assert.Null(textMatch.EntityId);
    }

    [Fact]
    public async Task UniqueWebhookConstraintRejectsDuplicatePair()
    {
        await fixture.ExecuteAsync("INSERT INTO platform.inbound_webhooks(id,provider,external_id,payload) VALUES ('00000000-0000-0000-0000-000000000020','provider','external','{}')");
        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("INSERT INTO platform.inbound_webhooks(id,provider,external_id,payload) VALUES ('00000000-0000-0000-0000-000000000021','provider','external','{}')"));
        Assert.Equal("23505", error.SqlState);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task UnitOfWorkCommitsOrRollsBack(bool saveEarly, bool fail, bool throwError)
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext())
        {
            var uow = new EfUnitOfWork(db);
            if (throwError)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync<int>(async ct =>
                {
                    db.Settings.Add(new Setting { Key = key, Value = "{}" });
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException("operation failed");
                }, CancellationToken.None));
            }
            else
            {
                var result = await uow.ExecuteAsync(async ct =>
                {
                    db.Settings.Add(new Setting { Key = key, Value = "{}" });
                    if (saveEarly) await db.SaveChangesAsync(ct);
                    return fail ? Result.Failure<int>(Error.BusinessRule("test", "failure")) : Result.Success(1);
                }, CancellationToken.None);
                Assert.Equal(!fail, result.IsSuccess);
            }
        }
        await using var reader = fixture.CreateContext();
        Assert.Equal(!fail && !throwError, await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Fact]
    public async Task UnitOfWorkCommitsMultipleSavesAndRollsBackOnPersistenceFailure()
    {
        var first = "test." + Guid.NewGuid().ToString("N");
        var second = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext())
        {
            var result = await new EfUnitOfWork(db).ExecuteAsync(async ct =>
            {
                db.Settings.Add(new Setting { Key = first, Value = "{}" });
                await db.SaveChangesAsync(ct);
                db.Settings.Add(new Setting { Key = second, Value = "{}" });
                await db.SaveChangesAsync(ct);
                return Result.Success(1);
            }, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }
        await using (var reader = fixture.CreateContext())
        {
            Assert.True(await reader.Settings.AnyAsync(x => x.Key == first));
            Assert.True(await reader.Settings.AnyAsync(x => x.Key == second));
        }
        var rolledBack = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(db).ExecuteAsync(async ct =>
            {
                db.Settings.Add(new Setting { Key = rolledBack, Value = "{}" });
                await db.SaveChangesAsync(ct);
                db.Settings.Add(new Setting { Key = first, Value = "{}" });
                return Result.Success(1);
            }, CancellationToken.None));
        }
        await using var verifier = fixture.CreateContext();
        Assert.False(await verifier.Settings.AnyAsync(x => x.Key == rolledBack));
    }

    [Fact]
    public async Task CancellationAfterSaveRollsBack()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        await using (var db = fixture.CreateContext())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EfUnitOfWork(db).ExecuteAsync<int>(async ct =>
            {
                db.Settings.Add(new Setting { Key = key, Value = "{}" });
                await db.SaveChangesAsync(ct);
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Result.Success(1);
            }, cancellation.Token));
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Fact]
    public async Task FailedResultRollsBackExactlyOnce()
    {
        var probe = new TransactionProbeInterceptor();
        var key = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext(probe))
        {
            var result = await new EfUnitOfWork(db).ExecuteAsync(async ct =>
            {
                db.Settings.Add(new Setting { Key = key, Value = "{}" });
                await db.SaveChangesAsync(ct);
                return Result.Failure<int>(Error.BusinessRule("test", "failure"));
            }, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(1, probe.RollbackAttempts);
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OriginalExceptionSurvivesRollback(bool cancel, bool failRollback)
    {
        var probe = new TransactionProbeInterceptor { FailRollback = failRollback };
        var key = "test." + Guid.NewGuid().ToString("N");
        using var source = new CancellationTokenSource();
        Exception original = cancel
            ? new OperationCanceledException("operation cancelled", source.Token)
            : new InvalidOperationException("operation failed");
        await using (var db = fixture.CreateContext(probe))
        {
            var observed = await Assert.ThrowsAnyAsync<Exception>(() => new EfUnitOfWork(db).ExecuteAsync<int>(
                ct => ThrowAfterSaveAsync(db, key, original, () => { if (cancel) source.Cancel(); }, ct), source.Token));
            Assert.Same(original, observed);
            Assert.Contains(nameof(ThrowAfterSaveAsync), observed.StackTrace, StringComparison.Ordinal);
            Assert.Equal(1, probe.RollbackAttempts);
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(observed);
            if (failRollback) Assert.Same(probe.RollbackError, observed.Data["TeaPremium.RollbackFailure"]);
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DomainExceptionThroughDecoratorSignalsRollbackOutcome(bool failRollback)
    {
        var probe = new TransactionProbeInterceptor { FailRollback = failRollback };
        var key = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext(probe))
        {
            var handler = new TransactionCommandDecorator<DomainFailureCommand, int>(
                new DomainFailureHandler(db), new EfUnitOfWork(db));

            if (failRollback)
            {
                var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
                    handler.Handle(new DomainFailureCommand(key), CancellationToken.None));
                Assert.IsNotType<DomainException>(observed);
                Assert.Equal("Transaction rollback failed; outcome is unknown.", observed.Message);
                var original = Assert.IsType<DomainException>(observed.InnerException);
                Assert.Equal("rule_failed", original.Code);
                Assert.Contains(nameof(DomainFailureHandler.Handle), original.StackTrace, StringComparison.Ordinal);
                Assert.Same(probe.RollbackError, observed.Data["TeaPremium.RollbackFailure"]);
                Assert.Contains(nameof(TransactionProbeInterceptor.TransactionRollingBackAsync),
                    probe.RollbackError.StackTrace, StringComparison.Ordinal);
                Assert.Equal(1, probe.RollbackAttempts);
            }
            else
            {
                var result = await handler.Handle(new DomainFailureCommand(key), CancellationToken.None);
                Assert.False(result.IsSuccess);
                Assert.Equal(ErrorType.BusinessRule, result.Error.Type);
                Assert.Equal("rule_failed", result.Error.Code);
                Assert.Equal(1, probe.RollbackAttempts);
            }
        }

        if (!failRollback)
        {
            await using var reader = fixture.CreateContext();
            Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
        }
        // A failed rollback does not prove whether the server committed or rolled back.
    }

    [Fact]
    public async Task FailedResultWithRollbackFailureThrowsWithoutRetryingRollback()
    {
        var probe = new TransactionProbeInterceptor { FailRollback = true };
        await using var db = fixture.CreateContext(probe);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfUnitOfWork(db).ExecuteAsync(
            ct => Task.FromResult(Result.Failure<int>(Error.BusinessRule("test", "failure"))), CancellationToken.None));
        Assert.Same(probe.RollbackError, observed);
        Assert.Equal(1, probe.RollbackAttempts);
    }

    [Fact]
    public async Task CommitFailureNeverReturnsSuccess()
    {
        var probe = new TransactionProbeInterceptor { FailCommit = true };
        var key = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext(probe))
        {
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfUnitOfWork(db).ExecuteAsync(
                ct =>
                {
                    db.Settings.Add(new Setting { Key = key, Value = "{}" });
                    return Task.FromResult(Result.Success(1));
                }, CancellationToken.None));
            Assert.Same(probe.CommitError, observed);
            Assert.Equal(1, probe.CommitAttempts);
            Assert.Equal(1, probe.RollbackAttempts);
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Fact]
    public async Task FailedOperationDoesNotLeakTrackedChangesIntoNextTransaction()
    {
        var rejected = "test." + Guid.NewGuid().ToString("N");
        var accepted = "test." + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreateContext())
        {
            var uow = new EfUnitOfWork(db);
            var failure = await uow.ExecuteAsync(ct =>
            {
                db.Settings.Add(new Setting { Key = rejected, Value = "{}" });
                return Task.FromResult(Result.Failure<int>(Error.BusinessRule("test", "failure")));
            }, CancellationToken.None);
            Assert.False(failure.IsSuccess);
            var success = await uow.ExecuteAsync(ct =>
            {
                db.Settings.Add(new Setting { Key = accepted, Value = "{}" });
                return Task.FromResult(Result.Success(1));
            }, CancellationToken.None);
            Assert.True(success.IsSuccess);
        }
        await using var reader = fixture.CreateContext();
        Assert.False(await reader.Settings.AnyAsync(x => x.Key == rejected));
        Assert.True(await reader.Settings.AnyAsync(x => x.Key == accepted));
    }

    [Fact]
    public async Task ScopedProductionRegistrationUsesRealDatabase()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = fixture.ConnectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddPlatformPersistence(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.IsType<EfUnitOfWork>(uow);
        var key = "test." + Guid.NewGuid().ToString("N");
        var result = await uow.ExecuteAsync(ct =>
        {
            db.Settings.Add(new Setting { Key = key, Value = "{}" });
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);
        Assert.True(result.IsSuccess);
        await using var reader = fixture.CreateContext();
        Assert.True(await reader.Settings.AnyAsync(x => x.Key == key));
    }

    [Fact]
    public async Task IdempotentScriptCanRunTwiceOnEmptyDisposableDatabase()
    {
        await using var disposable = new PostgreSqlBuilder("postgres:17").Build();
        await disposable.StartAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(disposable.GetConnectionString()).Options;
        await using var db = new AppDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);
        await using (var connection = new NpgsqlConnection(disposable.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using (var roles = new NpgsqlCommand("CREATE ROLE tp_app NOLOGIN; CREATE ROLE tp_readonly NOLOGIN", connection))
                await roles.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(10, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.tables WHERE table_schema='platform' AND table_type='BASE TABLE'").SingleAsync());
        await db.GetService<IMigrator>().MigrateAsync("0");
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.tables WHERE table_schema='platform' AND table_type='BASE TABLE'").SingleAsync());
    }

    private static async Task<Result<int>> ThrowAfterSaveAsync(
        AppDbContext db, string key, Exception original, Action beforeThrow, CancellationToken cancellationToken)
    {
        db.Settings.Add(new Setting { Key = key, Value = "{}" });
        await db.SaveChangesAsync(cancellationToken);
        beforeThrow();
        throw original;
    }

    private sealed record DomainFailureCommand(string Key) : ICommand<int>;

    private sealed class DomainFailureHandler(AppDbContext db) : ICommandHandler<DomainFailureCommand, int>
    {
        public async Task<Result<int>> Handle(DomainFailureCommand command, CancellationToken cancellationToken)
        {
            db.Settings.Add(new Setting { Key = command.Key, Value = "{}" });
            await db.SaveChangesAsync(cancellationToken);
            throw new DomainException("rule_failed", "Business value must stay private.");
        }
    }
}

internal sealed class TransactionProbeInterceptor : DbTransactionInterceptor
{
    public bool FailRollback { get; init; }
    public bool FailCommit { get; init; }
    public int RollbackAttempts { get; private set; }
    public int CommitAttempts { get; private set; }
    public Exception RollbackError { get; } = new InvalidOperationException("rollback failed");
    public Exception CommitError { get; } = new InvalidOperationException("commit failed");

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        RollbackAttempts++;
        if (FailRollback) throw RollbackError;
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        CommitAttempts++;
        if (FailCommit) throw CommitError;
        return ValueTask.FromResult(result);
    }
}
