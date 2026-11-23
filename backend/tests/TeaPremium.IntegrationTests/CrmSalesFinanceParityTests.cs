using System.Text.RegularExpressions;
using Npgsql;
using static TeaPremium.IntegrationTests.CrmSalesFinanceFixture;

namespace TeaPremium.IntegrationTests;

public sealed class CrmSalesFinanceParityTests(CrmSalesFinanceFixture fixture) : IClassFixture<CrmSalesFinanceFixture>
{
    private const string Scope = "('crm','sales','finance','pricing')";

    [Fact]
    public async Task EveryColumnConstraintIndexForeignKeyAndSequenceMatchesApprovedDdl()
    {
        var actual = await fixture.DatabaseAsync();
        var reference = await ReferenceAsync();
        var (expected, names) = await ExpectedAsync(actual, reference);
        Assert.Equal(expected, await CatalogAsync(actual));
        Assert.Equal(27, await ScalarAsync<int>(actual, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN " + Scope));
        Assert.Equal(0, await ScalarAsync<int>(actual, "SELECT count(*)::int FROM pg_tables WHERE schemaname = 'marketing'"));
        Assert.Equal(0, await ScalarAsync<int>(actual, "SELECT count(*)::int FROM pg_tables WHERE schemaname = 'inventory' AND tablename = 'reservations'"));
        // Exact implicit-name exceptions, retained for the local review inventory.
        await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "p201-naming-exceptions.md"), names);
    }

    [Theory]
    [InlineData("ALTER TABLE finance.expenses ALTER COLUMN amount_vnd TYPE integer")]
    [InlineData("ALTER TABLE crm.customers ALTER COLUMN zalo_name SET NOT NULL")]
    [InlineData("ALTER TABLE crm.customers ALTER COLUMN customer_group SET DEFAULT 'wholesale'")]
    [InlineData("ALTER TABLE crm.customers DROP CONSTRAINT ck_customer_contact; ALTER TABLE crm.customers ADD CONSTRAINT ck_customer_contact CHECK (contact_status = 'erased' OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL)")]
    [InlineData("DROP INDEX crm.ux_customers_phone; CREATE UNIQUE INDEX ux_customers_phone ON crm.customers(phone_e164) WHERE phone_e164 IS NOT NULL")]
    [InlineData("DROP INDEX crm.ux_account_email; CREATE UNIQUE INDEX ux_account_email ON crm.customer_accounts(email) WHERE email IS NOT NULL")]
    [InlineData("DROP INDEX crm.ux_customer_interest; CREATE UNIQUE INDEX ux_customer_interest ON crm.customer_interests(customer_id, interest_type, ref_id, price_band, source)")]
    [InlineData("ALTER TABLE crm.interactions DROP CONSTRAINT fk_interactions_order; ALTER TABLE crm.interactions ADD CONSTRAINT fk_interactions_order FOREIGN KEY (order_id) REFERENCES sales.orders(id) ON DELETE CASCADE")]
    [InlineData("ALTER SEQUENCE crm.customer_code_seq INCREMENT 2")]
    public async Task ParityDetectsSameNamedSemanticDrift(string mutation)
    {
        var actual = await fixture.DatabaseAsync();
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
        await ExecuteAsync(c, """
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA platform;
CREATE SCHEMA media;
CREATE SCHEMA catalog;
CREATE SCHEMA pricing;
CREATE SCHEMA crm;
CREATE SCHEMA sales;
CREATE SCHEMA finance;
CREATE TABLE platform.provinces (code text PRIMARY KEY);
CREATE TABLE platform.wards (code text PRIMARY KEY);
CREATE TABLE media.assets (id uuid PRIMARY KEY);
CREATE TABLE catalog.products (id uuid PRIMARY KEY);
CREATE TABLE catalog.variants (id uuid PRIMARY KEY);
""");
        // Parse only statement boundaries from the committed DDL, never EF/model SQL.
        // Defer ONLY the 4 owner-approved marketing.* FKs (P5-01/P5-02).
        var cleaned = Regex.Replace(ddl, @"--[^\n]*", "");
        var statements = Regex.Matches(cleaned, @"(?:CREATE (?:TABLE|(?:UNIQUE )?INDEX|SEQUENCE)|ALTER TABLE)\s+.*?;", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(s =>
                Regex.IsMatch(s, @"^CREATE TABLE (crm\.|sales\.|finance\.|pricing\.(sale_prices|vouchers|voucher_redemptions)\s)") ||
                Regex.IsMatch(s, @"^CREATE (?:UNIQUE )?INDEX \w+ ON (crm\.|sales\.|finance\.|pricing\.(sale_prices|vouchers|voucher_redemptions)\W)") ||
                s.StartsWith("CREATE SEQUENCE crm.customer_code_seq ", StringComparison.Ordinal) ||
                Regex.IsMatch(s, @"^ALTER TABLE (pricing\.vouchers|crm\.interactions)\s"))
            .Select(s =>
            {
                s = Regex.Replace(s, @",\s*ADD CONSTRAINT fk_interactions_campaign FOREIGN KEY \(campaign_id\) REFERENCES marketing\.campaigns\(id\) ON DELETE SET NULL", "");
                s = Regex.Replace(s, @"\s+REFERENCES marketing\.(tracking_links|campaigns)\(id\)", "");
                return s;
            });
        await ExecuteAsync(c, string.Join('\n', statements));
        Assert.Equal(27, await ScalarAsync<int>(c, "SELECT count(*)::int FROM pg_tables WHERE schemaname IN " + Scope));
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
