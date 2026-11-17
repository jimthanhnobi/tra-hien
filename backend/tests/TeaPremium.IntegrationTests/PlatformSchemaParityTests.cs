using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P0-10 / P0-04 / NFR-09: reuse the existing migrated PostgreSQL 17 fixture.
public sealed class PlatformSchemaParityTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string ReferenceSchema = "p010_reference";

    [Fact]
    public void ModelMatchesLastMigrationSnapshot()
    {
        using var db = fixture.CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ModelDifferenceDetectorRejectsTestOnlyColumnDrift()
    {
        using var db = fixture.CreateContext();
        // Rebuild the real internal snapshot into a separate test-only builder. Never
        // mutate db.Model or change accessibility of generated production migrations.
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var build = snapshot.GetType().GetMethod("BuildModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(build);
        var builder = new ModelBuilder();
        build.Invoke(snapshot, [builder]);
        builder.Entity(typeof(Setting).FullName!).Property<string>("p010_test_drift").HasColumnType("text");
        var changed = db.GetService<IModelRuntimeInitializer>().Initialize(builder.FinalizeModel(), designTime: true);
        var current = db.GetService<IDesignTimeModel>().Model;
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(current.GetRelationalModel(), changed.GetRelationalModel());
        Assert.Contains(differences.OfType<AddColumnOperation>(), operation => operation.Name == "p010_test_drift");
    }

    [Fact]
    public async Task MigratedPlatformMatchesAuthoritativeColumnsConstraintsIndexesAndFunction()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CreateReferenceAsync(connection, transaction);
        var migrated = await CatalogAsync(connection, transaction, "platform");
        var reference = await CatalogAsync(connection, transaction, ReferenceSchema);
        Assert.NotEmpty(migrated);
        Assert.Equal(reference, migrated);
        // Reference DDL exists only in this transaction and rolls back on disposal.
    }

    [Theory]
    [InlineData("ALTER TABLE p010_reference.outbox_messages ALTER COLUMN payload DROP NOT NULL", "column:outbox_messages:payload")]
    [InlineData("ALTER TABLE p010_reference.outbox_messages ALTER COLUMN attempts SET DEFAULT 1", "column:outbox_messages:attempts")]
    [InlineData("ALTER TABLE p010_reference.settings ALTER COLUMN updated_at TYPE timestamp without time zone", "column:settings:updated_at")]
    [InlineData("ALTER TABLE p010_reference.wards ALTER CONSTRAINT fk_wards_provinces_province_code DEFERRABLE", "constraint:wards:fk_wards_provinces_province_code")]
    [InlineData("ALTER SEQUENCE p010_reference.data_protection_keys_id_seq INCREMENT BY 2", "sequence:data_protection_keys_id_seq")]
    [InlineData("DROP INDEX p010_reference.ix_outbox_pending; CREATE INDEX ix_outbox_pending ON p010_reference.outbox_messages(available_at)", "index:outbox_messages:ix_outbox_pending")]
    public async Task CatalogComparisonDetectsSameNameButChangedSemantics(string mutation, string changedRow)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CreateReferenceAsync(connection, transaction);
        var migrated = await CatalogAsync(connection, transaction, "platform");
        Assert.Equal(migrated, await CatalogAsync(connection, transaction, ReferenceSchema));
        await using var alter = new NpgsqlCommand(mutation, connection, transaction);
        await alter.ExecuteNonQueryAsync();
        var changed = await CatalogAsync(connection, transaction, ReferenceSchema);
        var originalRow = Assert.Single(migrated, row => row.StartsWith(changedRow + "|", StringComparison.Ordinal));
        Assert.DoesNotContain(originalRow, changed);
        Assert.NotEqual(migrated, changed);
    }

    private static async Task CreateReferenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Specifications", "schema.sql"));
        var start = ddl.IndexOf("CREATE TABLE platform.settings", StringComparison.Ordinal);
        var end = ddl.IndexOf("CREATE TABLE media.assets", StringComparison.Ordinal);
        var functionStart = ddl.IndexOf("CREATE OR REPLACE FUNCTION public.f_unaccent", StringComparison.Ordinal);
        var functionEnd = ddl.IndexOf("CREATE SCHEMA platform", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start && functionStart >= 0 && functionEnd > functionStart,
            "The approved platform DDL boundary changed; review the P0-10 reference scope.");
        // Execute only the approved P0 platform tables, never the full future-phase DDL.
        var platform = ddl[start..end].Replace("platform.", ReferenceSchema + ".", StringComparison.Ordinal);
        var ledgerStart = ddl.IndexOf("CREATE TABLE platform.administrative_data_imports", StringComparison.Ordinal);
        Assert.True(ledgerStart > end, "The approved F-02 DDL must be available as a separate P0-09 block.");
        platform += ddl[ledgerStart..].Replace("platform.", ReferenceSchema + ".", StringComparison.Ordinal);
        var function = ddl[functionStart..functionEnd].Replace("FUNCTION public.f_unaccent", "FUNCTION " + ReferenceSchema + ".f_unaccent", StringComparison.Ordinal);
        await using var command = new NpgsqlCommand($"CREATE SCHEMA {ReferenceSchema};\n{platform}\n{function}", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> CatalogAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema)
    {
        const string sql = """
            SELECT 'column:' || t.relname || ':' || a.attname || '|' ||
                   format_type(a.atttypid,a.atttypmod) || '|' || a.attnotnull || '|' ||
                   coalesce(pg_get_expr(d.adbin,d.adrelid),'') || '|' || a.attidentity::text || '|' || a.attgenerated::text || '|' ||
                   CASE WHEN a.attcollation=0 THEN '' ELSE a.attcollation::regcollation::text END AS shape
            FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
            JOIN pg_attribute a ON a.attrelid=t.oid
            LEFT JOIN pg_attrdef d ON d.adrelid=t.oid AND d.adnum=a.attnum
            WHERE n.nspname=@schema AND t.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
            UNION ALL
            SELECT 'constraint:' || t.relname || ':' || c.conname || '|' || c.contype::text || '|' ||
                   pg_get_constraintdef(c.oid,true) || '|' || c.condeferrable || '|' || c.condeferred || '|' || c.convalidated
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid
            JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname=@schema
            UNION ALL
            SELECT 'index:' || tablename || ':' || indexname || '|' || indexdef
            FROM pg_indexes WHERE schemaname=@schema
            UNION ALL
            SELECT 'sequence:' || sequencename || '|' || data_type || '|' || start_value || '|' || min_value || '|' ||
                   max_value || '|' || increment_by || '|' || cycle || '|' || cache_size
            FROM pg_sequences WHERE schemaname=@schema
            UNION ALL
            SELECT 'function:' || p.proname || '|' || pg_get_functiondef(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname=CASE WHEN @schema='platform' THEN 'public' ELSE @schema END AND p.proname='f_unaccent'
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            var value = reader.GetString(0).Replace(schema + ".", "<schema>.", StringComparison.Ordinal);
            if (schema == "platform") value = value.Replace("FUNCTION public.f_unaccent", "FUNCTION <schema>.f_unaccent", StringComparison.Ordinal);
            result.Add(Regex.Replace(value, @"\s+", " ").Trim());
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

}
