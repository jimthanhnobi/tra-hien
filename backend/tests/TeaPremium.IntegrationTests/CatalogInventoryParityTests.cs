using System.Text.RegularExpressions;
using Npgsql;
using static TeaPremium.IntegrationTests.CatalogInventoryFixture;

namespace TeaPremium.IntegrationTests;

public sealed class CatalogInventoryParityTests(CatalogInventoryFixture fixture) : IClassFixture<CatalogInventoryFixture>
{
    private const string Scope = "('media','catalog','inventory','pricing','content')";

    private const string P1Head = "202611191437_P1_04_TaxonomyPublicationHistory";

    [Fact]
    public async Task EveryColumnConstraintIndexForeignKeyAndSequenceMatchesApprovedDdl()
    {
        var actual = await fixture.DatabaseAsync(target: P1Head);
        var reference = await ReferenceAsync();
        var (expected, names) = await ExpectedAsync(actual, reference);
        Assert.Equal(expected, await CatalogAsync(actual));
        // Exact implicit-name exceptions, retained for the local review inventory.
        await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "p101-naming-exceptions.md"), names);
    }

    [Theory]
    [InlineData("ALTER TABLE catalog.products ALTER COLUMN search_text SET DEFAULT 'changed'")]
    [InlineData("ALTER TABLE media.assets ALTER COLUMN width SET NOT NULL")]
    [InlineData("ALTER TABLE catalog.variant_attribute_values ALTER COLUMN value_number TYPE numeric(13,2)")]
    [InlineData("ALTER TABLE catalog.variants ALTER CONSTRAINT variants_product_id_fkey DEFERRABLE")]
    [InlineData("DROP INDEX catalog.ix_pav_filter_number; CREATE INDEX ix_pav_filter_number ON catalog.product_attribute_values(attribute_id,value_number)")]
    [InlineData("ALTER SEQUENCE catalog.sku_number_seq INCREMENT 2")]
    [InlineData("ALTER TABLE pricing.sale_prices DROP CONSTRAINT ex_sale_prices_no_overlap; ALTER TABLE pricing.sale_prices ADD CONSTRAINT ex_sale_prices_no_overlap EXCLUDE USING gist (variant_id WITH =,tstzrange(starts_at,ends_at,'[]') WITH &&)")]
    public async Task ParityDetectsSameNamedSemanticDrift(string mutation)
    {
        var actual = await fixture.DatabaseAsync(target: P1Head);
        var reference = await ReferenceAsync();
        var (expected, _) = await ExpectedAsync(actual, reference);
        Assert.Equal(expected, await CatalogAsync(actual));
        await ExecuteAsync(reference, mutation);
        var changed = await CatalogAsync(reference);
        // Compare after the already-verified exception map, so unrelated name
        // differences cannot make this detector pass without catching the mutation.
        var (normalized, _) = await ExpectedAsync(actual, reference, allowSemanticDrift: true);
        Assert.NotEqual(expected, normalized);
        Assert.NotEmpty(changed);
    }

    private async Task<string> ReferenceAsync()
    {
        var c = await fixture.DatabaseAsync(migrate: false);
        var ddl = await DdlAsync();
        await ExecuteAsync(c, "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS btree_gist; CREATE SCHEMA media; CREATE SCHEMA catalog; CREATE SCHEMA inventory; CREATE SCHEMA pricing; CREATE SCHEMA content;");
        // Parse only statement boundaries from the committed DDL, never EF/model SQL.
        var statements = Regex.Matches(Regex.Replace(ddl, @"--[^\n]*", ""), @"CREATE (?:TABLE|(?:UNIQUE )?INDEX|SEQUENCE)\s+.*?;", RegexOptions.Singleline)
            .Select(m => m.Value).Where(s =>
                Regex.IsMatch(s, @"^CREATE TABLE (media\.|catalog\.|inventory\.(stock_levels|stock_movements|receipts|receipt_lines|stocktakes|stocktake_lines)\s|pricing\.sale_prices\s|content\.redirects\s)") &&
                    !Regex.IsMatch(s, @"^CREATE TABLE catalog\.(product_reviews|review_media)\s") ||
                Regex.IsMatch(s, @"^CREATE (?:UNIQUE )?INDEX \w+ ON (media\.|catalog\.|inventory\.(stock_levels|stock_movements|receipts|receipt_lines|stocktakes|stocktake_lines)\W)") &&
                    !Regex.IsMatch(s, @" ON catalog\.(product_reviews|review_media)\W") ||
                s.StartsWith("CREATE SEQUENCE catalog.sku_number_seq ", StringComparison.Ordinal));
        await ExecuteAsync(c, string.Join('\n', statements));
        Assert.Equal(25, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN " + Scope));
        return c;
    }

    private static Task<string> DdlAsync() => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Specifications", "schema.sql"));

    private static async Task<(List<string> Expected, List<string> Names)> ExpectedAsync(string actual, string reference, bool allowSemanticDrift = false)
    {
        var target = await CatalogAsync(actual);
        var expected = await CatalogAsync(reference);
        var explicitNames = Regex.Matches(await DdlAsync(), @"CONSTRAINT (\w+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string> { "| Bảng | PostgreSQL implicit | EF explicit |", "|---|---|---|" };
        foreach (var row in expected.Where(x => x.StartsWith("constraint|", StringComparison.Ordinal)))
        {
            var fields = row.Split('|', 4);
            if (explicitNames.Contains(fields[2])) continue;
            var candidates = target.Where(x => x.StartsWith("constraint|" + fields[1] + "|", StringComparison.Ordinal) && x.Split('|', 4)[3] == fields[3]).ToArray();
            if (allowSemanticDrift && candidates.Length == 0) continue;
            var match = Assert.Single(candidates).Split('|', 4);
            Assert.Matches("^(pk|fk|ux|ck)_", match[2]);
            replacements.Add(fields[2], match[2]);
            names.Add($"| `{fields[1]}` | `{fields[2]}` | `{match[2]}` |");
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
        var sql = $$"""
SELECT 'column|'||n.nspname||'.'||t.relname||'|'||a.attname||'|'||format_type(a.atttypid,a.atttypmod)||'|'||a.attnotnull||'|'||
 coalesce(pg_get_expr(d.adbin,d.adrelid),'')||'|'||a.attidentity::text||'|'||a.attgenerated::text||'|'||
 CASE WHEN a.attcollation=0 THEN '' ELSE a.attcollation::regcollation::text END
FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace JOIN pg_attribute a ON a.attrelid=t.oid
LEFT JOIN pg_attrdef d ON d.adrelid=t.oid AND d.adnum=a.attnum
WHERE n.nspname IN {{Scope}} AND t.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
UNION ALL
SELECT 'constraint|'||n.nspname||'.'||t.relname||'|'||c.conname||'|'||c.contype::text||'|'||pg_get_constraintdef(c.oid,true)||'|'||
 c.condeferrable||'|'||c.condeferred||'|'||c.convalidated
FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname IN {{Scope}}
UNION ALL
SELECT 'index|'||schemaname||'.'||tablename||'|'||indexname||'|'||indexdef FROM pg_indexes WHERE schemaname IN {{Scope}}
UNION ALL
SELECT 'sequence|'||schemaname||'|'||sequencename||'|'||data_type||'|'||start_value||'|'||min_value||'|'||max_value||'|'||increment_by||'|'||cycle||'|'||cache_size
FROM pg_sequences WHERE schemaname IN {{Scope}}
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
}
