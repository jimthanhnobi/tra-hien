using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using static TeaPremium.IntegrationTests.CrmSalesFinanceFixture;

namespace TeaPremium.IntegrationTests;

public sealed class ReservationsMigrationTests(CrmSalesFinanceFixture fixture) : IClassFixture<CrmSalesFinanceFixture>
{
    [Fact]
    public async Task ReservationsTableIndexesAndForeignKeyMatchApprovedDdl()
    {
        var actual = await fixture.DatabaseAsync();
        var reference = await ReferenceAsync();
        var (expected, _) = await ExpectedAsync(actual, reference);
        Assert.Equal(expected, await CatalogAsync(actual));
    }

    [Theory]
    [InlineData("ALTER TABLE inventory.reservations ALTER COLUMN quantity TYPE bigint")]
    [InlineData("ALTER TABLE inventory.reservations ALTER COLUMN order_line_id SET NOT NULL")]
    [InlineData("ALTER TABLE inventory.reservations DROP CONSTRAINT reservations_check1; ALTER TABLE inventory.reservations ADD CONSTRAINT reservations_check1 CHECK (order_line_id IS NOT NULL)")]
    [InlineData("DROP INDEX inventory.ux_reservation_active_line; CREATE UNIQUE INDEX ux_reservation_active_line ON inventory.reservations(order_line_id)")]
    [InlineData("ALTER TABLE inventory.reservations DROP CONSTRAINT fk_reservations_line; ALTER TABLE inventory.reservations ADD CONSTRAINT fk_reservations_line FOREIGN KEY (order_line_id) REFERENCES sales.order_lines(id) ON DELETE CASCADE")]
    public async Task ReservationsParityDetectsSameNamedSemanticDrift(string mutation)
    {
        var actual = await fixture.DatabaseAsync();
        var reference = await ReferenceAsync();
        var (expected, _) = await ExpectedAsync(actual, reference);
        Assert.Equal(expected, await CatalogAsync(actual));

        await ExecuteAsync(reference, mutation);
        var (normalized, _) = await ExpectedAsync(actual, reference, allowSemanticDrift: true);
        Assert.NotEqual(expected, normalized);
    }

    [Fact]
    public async Task ModelHasNoPendingChangesAndP205MigrationMatchesAuthoringContract()
    {
        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);
        Assert.False(db.Database.HasPendingModelChanges());

        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations.Keys, key => key.EndsWith("_P2_05_InventoryReservations", StringComparison.Ordinal));
        Assert.True(string.CompareOrdinal(id, P201Head) > 0, $"Expected P2-05 migration id '{id}' after '{P201Head}'.");

        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("inventory", table.Schema);
        Assert.Equal("reservations", table.Name);
        Assert.DoesNotContain(table.Columns, col => col.Name is "xmin" or "version");

        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Contains(indexes, ix => ix.Name == "ux_reservation_active_line" && ix.IsUnique && ix.Filter == "status = 'active'");
        Assert.Contains(indexes, ix => ix.Name == "ix_reservation_variant_active" && !ix.IsUnique && ix.Filter == "status = 'active'");

        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        Assert.Equal(2, sql.Length);
        Assert.Equal(GrantsSql.Preflight, sql[0].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.Equal(GrantsSql.Reservations().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(),
            sql[^1].Sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
    }

    [Fact]
    public async Task IdempotentScriptExecutesTwiceAndPreservesReservationData()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        await using var db = Context(c);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteAsync(c, sql);
        await ExecuteAsync(c, CoreP2Seed + $"""
INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status)
VALUES ('00000000-0000-0000-0000-000000000251','{VariantId}','{OrderLineId}',1,'active');
""");

        await ExecuteAsync(c, sql);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT count(*)::int FROM inventory.reservations WHERE status='active'"));
    }

    [Fact]
    public async Task DownToP201HeadLeavesZeroLeftoverAndGuardRefusesDataOrExternalDependencies()
    {
        var p201Ref = await fixture.DatabaseAsync(target: P201Head);
        var p201Acl = await InventoryAclSnapshotAsync(p201Ref);

        var c = await fixture.DatabaseAsync();
        await using var db = Context(c);
        await db.GetService<IMigrator>().MigrateAsync(P201Head);
        Assert.Equal(7, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('inventory.reservations') IS NULL"));
        Assert.Equal(p201Acl, await InventoryAclSnapshotAsync(c));

        await db.Database.MigrateAsync();
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('inventory.reservations') IS NOT NULL"));

        // Refuse Down when reservations has even a released row with order_line_id IS NULL.
        await ExecuteAsync(c, CoreP2Seed + $"""
INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status,closed_at,close_reason)
VALUES ('00000000-0000-0000-0000-000000000252','{VariantId}',NULL,1,'released',now(),'line_removed');
""");
        var dataErr = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P201Head));
        Assert.Equal("P0001", dataErr.SqlState);
        Assert.Contains("P2-05 Down refused: data in inventory.reservations", dataErr.MessageText, StringComparison.Ordinal);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Clear row and test external dependency guard (view + extra FK).
        await ExecuteAsync(c, "DELETE FROM inventory.reservations; CREATE VIEW public.p205_outside_view AS SELECT id FROM inventory.reservations;");
        var depErr = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(P201Head));
        Assert.Equal("P0001", depErr.SqlState);
        Assert.Contains("object/dependency outside migration scope", depErr.MessageText, StringComparison.Ordinal);

        await ExecuteAsync(c, "DROP VIEW public.p205_outside_view;");
        await db.GetService<IMigrator>().MigrateAsync(P201Head);
        Assert.True(await ScalarAsync<bool>(c, "SELECT to_regclass('inventory.reservations') IS NULL"));
    }

    [Fact]
    public async Task RealTpAppTpReadonlyPreflightAndCheckConstraintsEnforceContract()
    {
        var preflightSql = await File.ReadAllTextAsync(Path.Combine(DatabaseProvisioning.Artifacts, "preflight.sql"));
        var c = await fixture.DatabaseAsync(target: P201Head);
        await ExecuteAsync(c, preflightSql);

        await using var db = Context(c);
        await db.Database.MigrateAsync();
        await ExecuteAsync(c, preflightSql);
        await ExecuteAsync(c, GrantsSql.Preflight);

        var app = DatabaseProvisioning.Login(c, "tp_app");
        var ro = DatabaseProvisioning.Login(c, "tp_readonly");
        await ExecuteAsync(app, CoreP2Seed);

        // tp_app can INSERT/SELECT/UPDATE/DELETE inventory.reservations; tp_readonly can SELECT only.
        await ExecuteAsync(app, $"""
INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status)
VALUES ('00000000-0000-0000-0000-000000000260','{VariantId}','{OrderLineId}',1,'active');
""");
        Assert.Equal(1, await ScalarAsync<int>(ro, "SELECT count(*)::int FROM inventory.reservations"));
        await ErrorAsync(ro, "DELETE FROM inventory.reservations", "42501");
        await ErrorAsync(app, "TRUNCATE inventory.reservations", "42501");

        // Probe ux_reservation_active_line -> 23505
        await ErrorAsync(app, $"""
INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status)
VALUES ('00000000-0000-0000-0000-000000000261','{VariantId}','{OrderLineId}',1,'active');
""", "23505", "ux_reservation_active_line");

        // Probe 4 CHECK constraints -> 23514
        await ErrorAsync(app, $"INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status) VALUES (gen_random_uuid(),'{VariantId}','{OrderLineId}',0,'active')", "23514", "ck_reservations_quantity");
        await ErrorAsync(app, $"INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status,closed_at) VALUES (gen_random_uuid(),'{VariantId}','{OrderLineId}',1,'bogus',now())", "23514", "ck_reservations_status");
        await ErrorAsync(app, $"INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status,closed_at) VALUES (gen_random_uuid(),'{VariantId}','{OrderLineId}',1,'active',now())", "23514", "ck_reservations_active_closed");
        await ErrorAsync(app, $"INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status,closed_at) VALUES (gen_random_uuid(),'{VariantId}',NULL,1,'consumed',now())", "23514", "ck_reservations_order_line_required");

        // Safety net: deleting sales.order_lines while reservation is 'active' fails 23514; after releasing it sets order_line_id = NULL.
        await ErrorAsync(app, $"DELETE FROM sales.order_lines WHERE id='{OrderLineId}'", "23514", "ck_reservations_order_line_required");
        await ExecuteAsync(app, $"""
UPDATE inventory.reservations SET status='released', closed_at=now(), close_reason='line_removed' WHERE order_line_id='{OrderLineId}';
DELETE FROM sales.order_lines WHERE id='{OrderLineId}';
""");
        Assert.Equal(1, await ScalarAsync<int>(ro, "SELECT count(*)::int FROM inventory.reservations WHERE status='released' AND order_line_id IS NULL"));
    }

    private async Task<string> ReferenceAsync()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Specifications", "schema.sql"));
        await ExecuteAsync(c, """
CREATE SCHEMA catalog;
CREATE SCHEMA inventory;
CREATE SCHEMA sales;
CREATE TABLE catalog.variants (id uuid PRIMARY KEY);
CREATE TABLE sales.order_lines (id uuid PRIMARY KEY);
""");
        var cleaned = Regex.Replace(ddl, @"--[^\n]*", "");
        var statements = Regex.Matches(cleaned, @"(?:CREATE (?:TABLE|(?:UNIQUE )?INDEX)|ALTER TABLE)\s+.*?;", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(s =>
                Regex.IsMatch(s, @"^CREATE TABLE inventory\.reservations\b") ||
                Regex.IsMatch(s, @"^CREATE (?:UNIQUE )?INDEX \w+ ON inventory\.reservations\W") ||
                Regex.IsMatch(s, @"^ALTER TABLE inventory\.reservations\s"));
        await ExecuteAsync(c, string.Join('\n', statements));
        return c;
    }

    private static async Task<(List<string> Expected, List<string> Names)> ExpectedAsync(string actual, string reference, bool allowSemanticDrift = false)
    {
        var target = await CatalogAsync(actual);
        var expected = await CatalogAsync(reference);
        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Specifications", "schema.sql"));
        var explicitNames = Regex.Matches(ddl, @"CONSTRAINT (\w+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var row in expected.Where(x => x.StartsWith("constraint|", StringComparison.Ordinal)))
        {
            var fields = row.Split('|', 4);
            if (explicitNames.Contains(fields[2])) continue;
            var candidates = target.Where(x => x.StartsWith("constraint|" + fields[1] + "|", StringComparison.Ordinal) && x.Split('|', 4)[3] == fields[3]).ToArray();
            if (allowSemanticDrift && candidates.Length == 0) continue;
            var match = Assert.Single(candidates).Split('|', 4);
            replacements.Add(fields[2], match[2]);
            names.Add($"{fields[2]} -> {match[2]}");
        }
        expected = expected.Select(row =>
        {
            foreach (var (before, after) in replacements)
                row = Regex.Replace(row, @"\b" + Regex.Escape(before) + @"\b", after);
            return row;
        }).Order(StringComparer.Ordinal).ToList();
        return (expected, names);
    }

    private static async Task<List<string>> CatalogAsync(string connection)
    {
        const string sql = """
SELECT 'column|'||n.nspname||'.'||t.relname||'|'||a.attname||'|'||format_type(a.atttypid,a.atttypmod)||'|'||a.attnotnull||'|'||
 coalesce(pg_get_expr(d.adbin,d.adrelid),'')||'|'||a.attidentity::text||'|'||a.attgenerated::text||'|'||
 CASE WHEN a.attcollation=0 THEN '' ELSE a.attcollation::regcollation::text END
FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace JOIN pg_attribute a ON a.attrelid=t.oid
LEFT JOIN pg_attrdef d ON d.adrelid=t.oid AND d.adnum=a.attnum
WHERE n.nspname='inventory' AND t.relname='reservations' AND t.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
UNION ALL
SELECT 'constraint|'||n.nspname||'.'||t.relname||'|'||c.conname||'|'||c.contype::text||'|'||pg_get_constraintdef(c.oid,true)||'|'||
 c.condeferrable||'|'||c.condeferred||'|'||c.convalidated
FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
WHERE n.nspname='inventory' AND t.relname='reservations'
UNION ALL
SELECT 'index|'||schemaname||'.'||tablename||'|'||indexname||'|'||indexdef
FROM pg_indexes WHERE schemaname='inventory' AND tablename='reservations'
""";
        await using var cn = new NpgsqlConnection(connection);
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(Regex.Replace(reader.GetString(0), @"\s+", " ").Trim());
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static Task<string> InventoryAclSnapshotAsync(string c) => ScalarAsync<string>(c, """
SELECT coalesce(string_agg(n.nspname||'.'||c.relname||':'||coalesce(c.relacl::text,''),',' ORDER BY n.nspname,c.relname),'')
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='inventory' AND c.relkind='r'
""");
}
