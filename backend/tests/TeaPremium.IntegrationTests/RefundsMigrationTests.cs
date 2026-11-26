using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CrmSalesFinanceFixture;

namespace TeaPremium.IntegrationTests;

public sealed class RefundsMigrationTests(CrmSalesFinanceFixture fixture) : IClassFixture<CrmSalesFinanceFixture>
{
    [Fact]
    public async Task ModelHasNoPendingChangesAndP207MigrationMatchesAuthoringContract()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);
        Assert.False(db.Database.HasPendingModelChanges());

        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations.Keys, key => key.EndsWith("_P2_07_RefundClassificationAndShipping", StringComparison.Ordinal));
        Assert.Equal(P207Head, id);
        Assert.True(string.CompareOrdinal(id, P205Head) > 0, $"Expected P2-07 migration id '{id}' after '{P205Head}'.");

        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);

        // Verify P2-07 Designer TargetModel matches the live design-time model with 0 differences
        // and directly defines Kind, ShippingAmountVnd, ux_refunds_return_request, and all 6 CHECK constraints.
        var targetModel = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        Assert.False(db.GetService<IMigrationsModelDiffer>().HasDifferences(
            targetModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel()));

        var refundEntity = targetModel.FindEntityType(typeof(Refund).FullName!);
        Assert.NotNull(refundEntity);

        var targetKind = refundEntity.FindProperty(nameof(Refund.Kind));
        Assert.NotNull(targetKind);
        Assert.False(targetKind.IsNullable);
        Assert.Null(targetKind.GetDefaultValue());
        Assert.Null(targetKind.GetDefaultValueSql());

        var targetShip = refundEntity.FindProperty(nameof(Refund.ShippingAmountVnd));
        Assert.NotNull(targetShip);
        Assert.False(targetShip.IsNullable);
        Assert.Equal("0", targetShip.GetDefaultValueSql());

        var targetIndex = Assert.Single(refundEntity.GetIndexes(), ix => ix.GetDatabaseName() == "ux_refunds_return_request");
        Assert.True(targetIndex.IsUnique);
        Assert.Equal("return_request_id IS NOT NULL", targetIndex.GetFilter());

        var targetChecks = refundEntity.GetCheckConstraints().Select(ck => ck.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, targetChecks.Count);
        foreach (var expectedCheck in new[]
        {
            "ck_refunds_amount_vnd",
            "ck_refunds_method",
            "ck_refunds_kind",
            "ck_refunds_shipping_amount_vnd",
            "ck_refunds_kind_return_request",
            "ck_refunds_kind_shipping"
        })
        {
            Assert.Contains(expectedCheck, targetChecks);
        }

        var addColumns = migration.UpOperations.OfType<AddColumnOperation>().ToArray();
        Assert.Equal(2, addColumns.Length);

        var kindCol = Assert.Single(addColumns, col => col.Name == "kind");
        Assert.Equal("sales", kindCol.Schema);
        Assert.Equal("refunds", kindCol.Table);
        Assert.Equal("text", kindCol.ColumnType);
        Assert.False(kindCol.IsNullable);
        Assert.Null(kindCol.DefaultValue);
        Assert.Null(kindCol.DefaultValueSql);

        var shipCol = Assert.Single(addColumns, col => col.Name == "shipping_amount_vnd");
        Assert.Equal("sales", shipCol.Schema);
        Assert.Equal("refunds", shipCol.Table);
        Assert.Equal("bigint", shipCol.ColumnType);
        Assert.False(shipCol.IsNullable);
        Assert.Null(shipCol.DefaultValue);
        Assert.Equal("0", shipCol.DefaultValueSql);

        var checks = migration.UpOperations.OfType<AddCheckConstraintOperation>().ToArray();
        Assert.Equal(4, checks.Length);
        Assert.Contains(checks, ck => ck.Name == "ck_refunds_kind");
        Assert.Contains(checks, ck => ck.Name == "ck_refunds_shipping_amount_vnd");
        Assert.Contains(checks, ck => ck.Name == "ck_refunds_kind_return_request");
        Assert.Contains(checks, ck => ck.Name == "ck_refunds_kind_shipping");

        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal("ux_refunds_return_request", index.Name);
        Assert.Equal("sales", index.Schema);
        Assert.Equal("refunds", index.Table);
        Assert.True(index.IsUnique);
        Assert.Equal("return_request_id IS NOT NULL", index.Filter);

        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        Assert.Equal(3, sql.Length);
        Assert.Equal(GrantsSql.Preflight, sql[0].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.Contains("DO $p207_up$", sql[1].Sql, StringComparison.Ordinal);
        Assert.Equal(GrantsSql.Preflight, sql[^1].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());

        // Verify live PostgreSQL catalog: kind has NO default, shipping_amount_vnd defaults to 0.
        Assert.True(await ScalarAsync<bool>(c, """
SELECT d.adbin IS NULL
FROM pg_attribute a
LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
WHERE a.attrelid = 'sales.refunds'::regclass AND a.attname = 'kind'
"""));
        Assert.Equal("0", await ScalarAsync<string>(c, """
SELECT pg_get_expr(d.adbin, d.adrelid)
FROM pg_attribute a
JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
WHERE a.attrelid = 'sales.refunds'::regclass AND a.attname = 'shipping_amount_vnd'
"""));
        Assert.Equal("i|true|1|1|true|true|(return_request_id IS NOT NULL)", await ScalarAsync<string>(c, """
SELECT c.relkind::text || '|' || i.indisunique::text || '|' || i.indnatts::text || '|' || i.indnkeyatts::text || '|' ||
       (i.indkey[0] = a.attnum)::text || '|' || (i.indexprs IS NULL)::text || '|' || coalesce(pg_get_expr(i.indpred, i.indrelid), '')
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attname = 'return_request_id' AND a.attnum > 0 AND NOT a.attisdropped
WHERE i.indexrelid = 'sales.ux_refunds_return_request'::regclass
"""));
    }

    [Fact]
    public async Task UpFailsClosedWhenSalesRefundsHasExistingRowsAtP205Head()
    {
        var c = await fixture.DatabaseAsync(target: P205Head);
        await ExecuteAsync(c, CoreP2Seed + $"""
INSERT INTO sales.refunds(id,order_id,amount_vnd,method,reason,refunded_at,created_by)
VALUES ('00000000-0000-0000-0000-000000000271','{OrderId}',100000,'bank_transfer','Legacy refund before P2-07',now(),'{StaffId}');
""");

        await using var db = Context(c);
        var err = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal("P0001", err.SqlState);
        Assert.Contains("P2-07 Up refused: existing rows in sales.refunds", err.MessageText, StringComparison.Ordinal);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema='sales' AND table_name='refunds' AND column_name IN ('kind','shipping_amount_vnd')"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM sales.refunds"));

        // After explicit cleanup (empty sales.refunds), upgrade to P207Head succeeds cleanly.
        await ExecuteAsync(c, "DELETE FROM sales.refunds;");
        await db.Database.MigrateAsync();
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(2, await ScalarAsync<int>(c, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema='sales' AND table_name='refunds' AND column_name IN ('kind','shipping_amount_vnd')"));
    }

    [Fact]
    public async Task IdempotentScriptExecutesTwiceAndPreservesClassifiedRefundData()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        await using var db = Context(c);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        await ExecuteAsync(c, sql);
        await ExecuteAsync(c, CoreP2Seed + $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES ('00000000-0000-0000-0000-000000000272','{OrderId}','overpayment',50000,0,'bank_transfer','Overpayment refund',now(),'{StaffId}');
""");

        // Second execution of idempotent script must skip P2-07 via __EFMigrationsHistory and keep existing refund rows intact.
        await ExecuteAsync(c, sql);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM sales.refunds WHERE kind='overpayment' AND shipping_amount_vnd=0"));
    }

    [Fact]
    public async Task DownToP205HeadGuardRefusesDataOrExternalDependenciesAndDownUpRecovers()
    {
        var p205Ref = await fixture.DatabaseAsync(target: P205Head);
        var p205Acl = await RefundsAclAsync(p205Ref);

        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);

        // Clean Down to P205Head when sales.refunds is empty.
        await db.GetService<IMigrator>().MigrateAsync(P205Head);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT count(*)::int FROM information_schema.columns WHERE table_schema='sales' AND table_name='refunds' AND column_name IN ('kind','shipping_amount_vnd')"));
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('sales.ux_refunds_return_request') IS NULL"));
        Assert.Equal(p205Acl, await RefundsAclAsync(c));

        // Up back to P207Head.
        await db.Database.MigrateAsync();
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('sales.ux_refunds_return_request') IS NOT NULL"));

        // Guard 1: refuse Down when sales.refunds contains data.
        await ExecuteAsync(c, CoreP2Seed + $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES ('00000000-0000-0000-0000-000000000273','{OrderId}','cancellation',100000,0,'cash','Cancel refund',now(),'{StaffId}');
""");
        var dataErr = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P205Head));
        Assert.Equal("P0001", dataErr.SqlState);
        Assert.Contains("P2-07 Down refused: data in sales.refunds", dataErr.MessageText, StringComparison.Ordinal);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Guard 2: refuse Down when an external view depends on kind/shipping_amount_vnd.
        await ExecuteAsync(c, "DELETE FROM sales.refunds; CREATE VIEW public.p207_outside_view AS SELECT id, kind, shipping_amount_vnd FROM sales.refunds;");
        var depErr = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P205Head));
        Assert.Equal("P0001", depErr.SqlState);
        Assert.Contains("object/dependency outside migration scope", depErr.MessageText, StringComparison.Ordinal);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Drop external dependency -> Down -> Up succeeds cleanly.
        await ExecuteAsync(c, "DROP VIEW public.p207_outside_view;");
        await db.GetService<IMigrator>().MigrateAsync(P205Head);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await db.Database.MigrateAsync();
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task DownRefusesDriftedUxRefundsReturnRequestAndPreservesExternalIndex()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);

        // Simulate schema drift: replace sales.ux_refunds_return_request with an identically named index on sales.payments.
        await ExecuteAsync(c, """
DROP INDEX sales.ux_refunds_return_request;
CREATE INDEX ux_refunds_return_request ON sales.payments(order_id);
""");

        var err = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P205Head));
        Assert.Equal("P0001", err.SqlState);
        Assert.Contains("ux_refunds_return_request missing or drifted outside migration scope", err.MessageText, StringComparison.Ordinal);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Verify the external index on sales.payments was NOT dropped by Down.
        Assert.Equal("payments", await ScalarAsync<string>(c, """
SELECT tbl.relname
FROM pg_index i
JOIN pg_class idx ON idx.oid = i.indexrelid
JOIN pg_class tbl ON tbl.oid = i.indrelid
JOIN pg_namespace ns ON ns.oid = idx.relnamespace
WHERE ns.nspname = 'sales' AND idx.relname = 'ux_refunds_return_request'
"""));
    }

    [Fact]
    public async Task DownRefusesDriftedCheckConstraintAndPreservesExternalConstraint()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);

        // Simulate schema drift on empty sales.refunds: replace ck_refunds_kind with an external CHECK (amount_vnd > 0).
        await ExecuteAsync(c, """
ALTER TABLE sales.refunds DROP CONSTRAINT ck_refunds_kind;
ALTER TABLE sales.refunds ADD CONSTRAINT ck_refunds_kind CHECK (amount_vnd > 0);
""");

        var err = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P205Head));
        Assert.Equal("P0001", err.SqlState);
        Assert.Contains("expected P2-07 CHECK constraint missing or drifted outside migration scope", err.MessageText, StringComparison.Ordinal);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Verify the external ck_refunds_kind constraint on sales.refunds was NOT dropped by Down.
        Assert.Equal("CHECK ((amount_vnd > 0))", await ScalarAsync<string>(c, """
SELECT pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'sales.refunds'::regclass
  AND contype = 'c'
  AND conname = 'ck_refunds_kind'
"""));

        // Also verify a missing P2-07 CHECK constraint is refused by Down.
        await ExecuteAsync(c, "ALTER TABLE sales.refunds DROP CONSTRAINT ck_refunds_kind;");
        var missingErr = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P205Head));
        Assert.Equal("P0001", missingErr.SqlState);
        Assert.Contains("expected P2-07 CHECK constraint missing or drifted outside migration scope", missingErr.MessageText, StringComparison.Ordinal);
        Assert.Equal(9, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task RealTpAppTpReadonlyPreflightAndAllRefundConstraintsEnforceContract()
    {
        var preflightSql = await File.ReadAllTextAsync(Path.Combine(DatabaseProvisioning.Artifacts, "preflight.sql"));
        var c = await fixture.DatabaseAsync(target: P205Head);
        await ExecuteAsync(c, preflightSql);

        await using var db = Context(c);
        await db.Database.MigrateAsync();
        await ExecuteAsync(c, preflightSql);
        await ExecuteAsync(c, GrantsSql.Preflight);

        var app = DatabaseProvisioning.Login(c, "tp_app");
        var ro = DatabaseProvisioning.Login(c, "tp_readonly");
        await ExecuteAsync(app, CoreP2Seed);

        // Valid inserts across all 4 kinds (including NULL return_request_id multiple times and 1 return_request_id).
        await ExecuteAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES
  ('00000000-0000-0000-0000-000000000281','{OrderId}',NULL,'overpayment',50000,0,'bank_transfer','Overpaid',now(),'{StaffId}'),
  ('00000000-0000-0000-0000-000000000282','{OrderId}',NULL,'cancellation',100000,0,'bank_transfer','Cancelled',now(),'{StaffId}'),
  ('00000000-0000-0000-0000-000000000283','{OrderId}',NULL,'adjustment',80000,30000,'cash','Goodwill',now(),'{StaffId}'),
  ('00000000-0000-0000-0000-000000000284','{OrderId}','{ReturnRequestId}','return',200000,30000,'bank_transfer','Return',now(),'{StaffId}');
""");

        // tp_readonly can SELECT new columns; cannot write. tp_app cannot DELETE/TRUNCATE sales.refunds.
        Assert.Equal(4, await ScalarAsync<int>(ro, "SELECT count(*)::int FROM sales.refunds WHERE kind IS NOT NULL AND shipping_amount_vnd >= 0"));
        await ErrorAsync(ro, "DELETE FROM sales.refunds", "42501");
        await ErrorAsync(app, "DELETE FROM sales.refunds", "42501");
        await ErrorAsync(app, "TRUNCATE sales.refunds", "42501");

        // Omitting kind fails NOT NULL (23502) because kind has NO default.
        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}',10000,'cash','No kind',now(),'{StaffId}')
""", "23502");

        // Probe all 4 CHECK constraints -> 23514.
        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','other',10000,0,'cash','Bad kind',now(),'{StaffId}')
""", "23514", "ck_refunds_kind");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','adjustment',10000,-1,'cash','Negative ship',now(),'{StaffId}')
""", "23514", "ck_refunds_shipping_amount_vnd");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','adjustment',10000,10001,'cash','Ship exceeds amount',now(),'{StaffId}')
""", "23514", "ck_refunds_shipping_amount_vnd");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}',NULL,'return',10000,0,'cash','Return without id',now(),'{StaffId}')
""", "23514", "ck_refunds_kind_return_request");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','{ReturnRequestId}','adjustment',10000,0,'cash','Adjustment with return id',now(),'{StaffId}')
""", "23514", "ck_refunds_kind_return_request");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','overpayment',10000,1000,'cash','Overpayment shipping',now(),'{StaffId}')
""", "23514", "ck_refunds_kind_shipping");

        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','cancellation',10000,1000,'cash','Cancellation shipping',now(),'{StaffId}')
""", "23514", "ck_refunds_kind_shipping");

        // Probe ux_refunds_return_request -> 23505 on second refund for the same return_request_id.
        await ErrorAsync(app, $"""
INSERT INTO sales.refunds(id,order_id,return_request_id,kind,amount_vnd,shipping_amount_vnd,method,reason,refunded_at,created_by)
VALUES (gen_random_uuid(),'{OrderId}','{ReturnRequestId}','return',10000,0,'cash','Duplicate return refund',now(),'{StaffId}')
""", "23505", "ux_refunds_return_request");
    }

    private static Task<string> RefundsAclAsync(string c) => ScalarAsync<string>(c, """
SELECT coalesce(relacl::text, '') FROM pg_class WHERE oid = 'sales.refunds'::regclass
""");
}
