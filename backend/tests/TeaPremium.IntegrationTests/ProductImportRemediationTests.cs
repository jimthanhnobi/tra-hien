using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class ProductImportTests
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidReferencesStayRowLocalAndDoNotBlockOtherGroups(bool preview)
    {
        await using var h = await ProductHarness.Create(fixture);
        var before = await Fingerprint(h);
        const string csv = "product_slug,name,product_type_code,material_slug,category_slug,artisan_slug,sku\n" +
            "before,Before,p105_teapot\n" +
            "bad,Typed,p105_teapot\0\n" +
            "bad-material,Material,p105_teapot,p105-clay\0\n" +
            "bad-category,Category,p105_teapot,,BAD CATEGORY\n" +
            "bad-artisan,Artisan,p105_teapot,,,not-found\n" +
            "bad-sku,Manual,p105_teapot,,,,BAD\0SKU\n" +
            "bad-format,Format,p105_teapot,,,,lower-case\n" +
            "later-row,Grouped,p105_teapot,p105-clay,p105-tea\n" +
            "later-row,Grouped,p105_teapot,,missing-category,bad\0artist\n" +
            "after,After,p105_teapot";
        var result = await Import(h, csv, preview);
        Assert.Equal(7, result.Summary.Failed);
        Assert.Equal(0, result.Summary.Unknown);
        Assert.Equal(0, result.Summary.NotProcessed);
        Assert.Equal(preview ? 2 : 0, result.Summary.Ready);
        Assert.Equal(preview ? 0 : 2, result.Summary.Committed);
        AssertRowError(result.Rows, 3, "product_type_code", "invalid_text");
        AssertRowError(result.Rows, 4, "material_slug", "invalid_text");
        AssertRowError(result.Rows, 5, "category_slug", "invalid_slug");
        AssertRowError(result.Rows, 6, "artisan_slug", "unknown_artisan");
        AssertRowError(result.Rows, 7, "sku", "invalid_text");
        AssertRowError(result.Rows, 8, "sku", "invalid_sku");
        AssertRowError(result.Rows, 10, "category_slug", "unknown_category");
        AssertRowError(result.Rows, 10, "artisan_slug", "invalid_text");
        if (preview) Assert.Equal(before, await Fingerprint(h));
        else
        {
            Assert.Equal("before,after", await Scalar(h, "SELECT string_agg(slug, ',' ORDER BY sku_base) FROM catalog.products"));
            Assert.Equal("2", await Scalar(h, "SELECT count(*)::text FROM catalog.variants"));
            Assert.Equal("2", await Scalar(h, "SELECT count(*)::text FROM platform.audit_logs WHERE entity_type='catalog.products'"));
            Assert.Equal("2", await Scalar(h, "SELECT count(*)::text FROM platform.outbox_messages WHERE type='StorefrontInvalidate'"));
            Assert.Equal("2", await Scalar(h, "SELECT last_value::text FROM catalog.sku_number_seq"));
        }
    }

    [Fact]
    public async Task ACommitWithoutAcknowledgementStillStopsFollowingGroups()
    {
        var interceptor = new LostAcknowledgement();
        await using var h = await ProductHarness.Create(fixture,
            services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)));
        interceptor.Armed = true;
        var result = await Import(h, Headers + "maybe,Maybe,p105_teapot\nlater,Later,p105_teapot", false);
        Assert.Equal("unknown", result.Groups[0].Status);
        Assert.Equal("notProcessed", result.Groups[1].Status);
        Assert.Equal("maybe", await Scalar(h, "SELECT slug FROM catalog.products"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsvAndNumericXlsxCellsValidateOriginalDecimalBeforeParsing(bool xlsx)
    {
        await using var h = await ProductHarness.Create(fixture);
        string[][] data =
        [
            ["product_slug", "name", "product_type_code", "price_vnd", "v.capacity_ml"],
            ["tiny-tail", "Tiny tail", "p105_teapot", "", "1.0000000000000000000000000000001"],
            ["extra-scale", "Extra scale", "p105_teapot", "", "2.3010"],
            ["max", "Max", "p105_teapot", "10000000000", "9999999999.99"],
            ["too-large", "Too large", "p105_teapot", "", "10000000000"],
            ["integer", "Integer", "p105_teapot", "42", "3"],
            ["decimal", "Decimal", "p105_teapot", "", "4.25"],
            ["trailing-zero", "Trailing zero", "p105_teapot", "", "000000005.120000000000000000000000000000000000"],
            ["scientific", "Scientific", "p105_teapot", "", "1E2"],
            ["fractional-price", "Fractional price", "p105_teapot", "42.00", "1"],
            ["scientific-price", "Scientific price", "p105_teapot", "4e2", "1"]
        ];
        var bytes = xlsx ? Workbook(data, numericColumn: 4) :
            Encoding.UTF8.GetBytes(string.Join('\n', data.Select(row => string.Join(',', row))));
        var file = xlsx ? "precision.xlsx" : "precision.csv";
        var before = await Fingerprint(h);
        var preview = await Import(h, bytes, file, true);
        Assert.Equal(before, await Fingerprint(h));
        var apply = await Import(h, bytes, file, false);
        Assert.Equal(4, preview.Summary.Ready);
        Assert.Equal(4, apply.Summary.Committed);
        foreach (var result in new[] { preview, apply })
        {
            foreach (var row in new[] { 2, 3, 5, 9 })
                AssertRowError(result.Rows, row, "v.capacity_ml", "invalid_attribute");
            AssertRowError(result.Rows, 10, "price_vnd", "invalid_price");
            AssertRowError(result.Rows, 11, "price_vnd", "invalid_price");
        }
        var expected = new[] { 9999999999.99m, 3m, 4.25m, 5.12m };
        var committed = apply.Groups.Where(g => g.ProductId is not null).ToArray();
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Assert.Single(Assert.Single((await h.Get(committed[i].ProductId!.Value)).Variants).Attributes).ValueNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRowsDoNotUseQuotaAndDataErrorsKeepPhysicalPositions(bool xlsx)
    {
        await using var h = await ProductHarness.Create(fixture);
        string[] header = ["product_slug", "name", "product_type_code"];
        string[][] small = [header, [], ["valid", "Valid", "p105_teapot"], [" ", "\t", " "], ["", "", ""], ["partial"], .. Enumerable.Repeat(Array.Empty<string>(), 510)];
        byte[] Encode(string[][] rows) => xlsx ? Workbook(rows) :
            Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(row => string.Join(',', row))) + "\n");
        var file = xlsx ? "empty.xlsx" : "empty.csv";
        var result = await Import(h, Encode(small), file, true);
        Assert.Equal([3, 6], result.Rows.Select(r => r.Row).ToArray());
        AssertRowError(result.Rows, 6, "product_type_code", "required");
        AssertRowError(result.Rows, 6, "name", "invalid_name");
        var one = await Import(h, Encode([header, small[2], .. Enumerable.Repeat(Array.Empty<string>(), 500)]), file, true);
        Assert.Single(one.Rows);
        string[][] exactRows = [header, .. Enumerable.Repeat(small[2], 500)];
        Assert.Equal(500, (await Import(h, Encode(exactRows), file, true)).Summary.Rows);
        using var overflow = await Upload(h.App.Client, Encode([.. exactRows, small[2]]), file, "true");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overflow.StatusCode);
        using var problem = JsonDocument.Parse(await overflow.Content.ReadAsStringAsync());
        Assert.Equal(502, problem.RootElement.GetProperty("errors")[0].GetProperty("row").GetInt32());
        Assert.Equal("0", await Scalar(h, "SELECT count(*)::text FROM catalog.products"));
    }

    [Fact]
    public async Task WorksheetStreamsSparseCoordinatesSharedStringsAndStopsBeforeInvalidTail()
    {
        await using var h = await ProductHarness.Create(fixture);
        const string shared = $"<sst xmlns=\"{SpreadsheetNamespace}\"><si><t>name</t></si><si><r><t>Rich</t></r><r><t xml:space=\"preserve\"> name</t></r></si></sst>";
        var headers = "<c r=\"A1\" t=\"s\"><v>0</v></c>" + Inline("B1", "product_type_code") +
            string.Concat(Enumerable.Range(3, 98).Select(i => Inline(Column(i) + "1", $"v.extra_{i}")));
        var doc = $"<worksheet xmlns=\"{SpreadsheetNamespace}\"><dimension ref=\"A1:XFD1048576\"/><sheetData>" +
            $"<row r=\"1\">{headers}</row><row r=\"899999\"/><row r=\"900000\"><c r=\"A900000\" t=\"s\"><v>1</v></c>" +
            Inline("B900000", "p105_teapot") + "<c r=\"CV900000\"/></row></sheetData></worksheet>";
        var result = await Import(h, Workbook([], worksheetXml: doc, shared: shared), "sparse.xlsx", true);
        Assert.Equal(900000, Assert.Single(result.Rows).Row);
        Assert.Equal("Rich name", Assert.Single(result.Groups).Name);

        var limit = $"<worksheet xmlns=\"{SpreadsheetNamespace}\"><sheetData><row r=\"1\">{headers}</row>" +
            $"<row r=\"2\">{Inline("A2", "Valid")}{Inline("B2", "p105_teapot")}<c r=\"CW2\"/><BROKEN";
        using var oversized = await Upload(h.App.Client, Workbook([], worksheetXml: limit, shared: shared), "wide.xlsx", "true");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var details = JsonDocument.Parse(await oversized.Content.ReadAsStringAsync());
        Assert.Equal("CW2", details.RootElement.GetProperty("errors")[0].GetProperty("column").GetString());

        var dtd = "<!DOCTYPE worksheet [<!ENTITY name SYSTEM \"https://example.invalid/ignored\">]>" + doc;
        using var invalid = await Upload(h.App.Client, Workbook([], worksheetXml: dtd, shared: shared), "dtd.xlsx", "true");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ApprovedTemplatesStillPreviewAndCommit(string extension)
    {
        await using var h = await ProductHarness.Create(fixture);
        await h.App.ExecuteAsync($"UPDATE catalog.product_types SET code='teapot' WHERE id='{h.Type}'; UPDATE catalog.product_types SET code='cup' WHERE id='{h.OtherType}'");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pnpm-workspace.yaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var file = "p1-07-products." + extension;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "docs", "templates", file));
        Assert.Equal(2, (await Import(h, bytes, file, true)).Summary.Ready);
        var applied = await Import(h, bytes, file, false);
        Assert.Equal(2, applied.Summary.Committed);
        Assert.Equal(3, applied.Rows.Count);
        Assert.Equal("Ấm trà mẫu", applied.Groups[0].Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedPricesAndSkusAreAbsentFromActualResponse(bool preview)
    {
        await using var h = await ProductHarness.Create(fixture);
        var email = await CreateStaff(h, "content", true);
        await h.App.LoginAsync(email, P009Harness.Password + "-Changed");
        const string csv = "product_slug,name,product_type_code,price_vnd,sku\n" +
            "price,Price,p105_teapot,987654321\n" +
            "sku,Sku,p105_teapot,,MANUAL-ECHO-ONLY\n" +
            "both,Both,p105_teapot,876543219,MANUAL-ECHO-BOTH";
        var before = await Fingerprint(h);
        using var response = await Upload(h.App.Client, Encoding.UTF8.GetBytes(csv), "fields.csv", preview ? "true" : "false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("987654321", body, StringComparison.Ordinal);
        Assert.DoesNotContain("876543219", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MANUAL-ECHO", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(3, root.GetProperty("summary").GetProperty("failed").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray();
        var nestedRows = root.GetProperty("groups").EnumerateArray().SelectMany(group => group.GetProperty("rows").EnumerateArray());
        foreach (var row in rows.Concat(nestedRows))
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("priceVnd").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("sku").ValueKind);
            var errors = row.GetProperty("errors");
            Assert.Equal(row.GetProperty("row").GetInt32() == 4 ? 2 : 1, errors.GetArrayLength());
            Assert.All(errors.EnumerateArray(), e => Assert.Equal("forbidden", e.GetProperty("code").GetString()));
        }
        Assert.Equal(before, await Fingerprint(h));

        // A pricing permission allows price data even without the owner role.
        await using (var db = h.App.OwnerContext())
        {
            var id = await db.Roles.Where(role => role.Name == "content").Select(role => role.Id).SingleAsync();
            db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = id, ClaimType = "permission", ClaimValue = "pricing.write" });
            await db.SaveChangesAsync();
        }
        await h.App.LoginAsync(email, P009Harness.Password + "-Changed");
        var priced = await Import(h, csv, preview);
        Assert.Equal(987654321, priced.Rows[0].PriceVnd);
        Assert.Null(priced.Rows[1].Sku);
        AssertRowError(priced.Rows, 3, "sku", "forbidden");

        await h.App.LoginAsync();
        var owner = await Import(h, "product_slug,name,product_type_code,price_vnd,sku\nowner,Owner,p105_teapot,123,OWNER-MANUAL", preview);
        Assert.Equal("OWNER-MANUAL", Assert.Single(owner.Rows).Sku);
        Assert.Equal(123, owner.Rows[0].PriceVnd);
        Assert.Equal(preview ? 0 : 1, owner.Summary.Committed);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task PackageDimensionsRejectLossyValuesAndPreserveExactValues(int? numericColumn, bool xlsx)
    {
        await using var h = await ProductHarness.Create(fixture);
        const string overprecise = "1.0000000000000000000000000000001";
        const string smallest = "0.0000000000000000000000000001";
        string[][] data =
        [
            ["product_slug", "name", "product_type_code", "package_l_cm", "package_w_cm", "package_h_cm"],
            ["exact", "Exact", "p105_teapot", "+0000001.23456789012345678901234567890000", smallest, "79228162514264337593543950335.00"],
            ["bad-l", "Bad length", "p105_teapot", overprecise],
            ["bad-w", "Bad width", "p105_teapot", "", "7922816251426433759354395033.6"],
            ["bad-h", "Bad height", "p105_teapot", "", "", "0.00000000000000000000000000001"],
            ["overflow", "Overflow", "p105_teapot", "79228162514264337593543950336"],
            ["exponent", "Exponent", "p105_teapot", "", "1e2"],
            ["bad-syntax", "Bad syntax", "p105_teapot", "", "", "1.2.3"],
            ["partial", "Partial", "p105_teapot", "", "-0.0000000000000000000000000000000", "-.12500"],
            ["blank", "Blank", "p105_teapot"]
        ];
        var bytes = xlsx ? Workbook(data, numericColumn: numericColumn) : EncodeCsv(data);
        var file = xlsx ? "package.xlsx" : "package.csv";
        var before = await Fingerprint(h);
        var preview = await Import(h, bytes, file, true);
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal(3, preview.Summary.Ready);
        var apply = await Import(h, bytes, file, false);
        Assert.Equal(3, apply.Summary.Committed);
        foreach (var result in new[] { preview, apply })
        {
            Assert.Equal(6, result.Summary.Failed);
            Assert.Equal(0, result.Summary.Unknown);
            AssertRowError(result.Rows, 3, "package_l_cm", "invalid_dimension");
            AssertRowError(result.Rows, 4, "package_w_cm", "invalid_dimension");
            AssertRowError(result.Rows, 5, "package_h_cm", "invalid_dimension");
            AssertRowError(result.Rows, 6, "package_l_cm", "invalid_dimension");
            AssertRowError(result.Rows, 7, "package_w_cm", "invalid_dimension");
            AssertRowError(result.Rows, 8, "package_h_cm", "invalid_dimension");
            var exact = result.Rows[0].PackageDimensions!.Value;
            Assert.Equal(1.2345678901234567890123456789m, exact.GetProperty("l").GetDecimal());
            Assert.Equal(0.0000000000000000000000000001m, exact.GetProperty("w").GetDecimal());
            Assert.Equal(decimal.MaxValue, exact.GetProperty("h").GetDecimal());
            var partial = result.Rows[7].PackageDimensions!.Value;
            Assert.False(partial.TryGetProperty("l", out _));
            Assert.Equal(0m, partial.GetProperty("w").GetDecimal());
            Assert.Equal(-0.125m, partial.GetProperty("h").GetDecimal());
            Assert.Null(result.Rows[8].PackageDimensions);
        }

        Assert.Equal(smallest, await Scalar(h, "SELECT v.package_dims_cm ->> 'w' FROM catalog.variants v JOIN catalog.products p ON p.id=v.product_id WHERE p.slug='exact'"));
        Assert.Equal("1.2345678901234567890123456789", await Scalar(h, "SELECT v.package_dims_cm ->> 'l' FROM catalog.variants v JOIN catalog.products p ON p.id=v.product_id WHERE p.slug='exact'"));
        Assert.Equal("79228162514264337593543950335", await Scalar(h, "SELECT v.package_dims_cm ->> 'h' FROM catalog.variants v JOIN catalog.products p ON p.id=v.product_id WHERE p.slug='exact'"));
        Assert.Equal("blank,exact,partial", await Scalar(h, "SELECT string_agg(slug, ',' ORDER BY slug) FROM catalog.products"));
        Assert.Equal("3", await Scalar(h, "SELECT count(*)::text FROM catalog.variants"));
        Assert.Equal("3", await Scalar(h, "SELECT count(*)::text FROM platform.audit_logs WHERE entity_type='catalog.products'"));
        Assert.Equal("3", await Scalar(h, "SELECT count(*)::text FROM platform.outbox_messages WHERE type='StorefrontInvalidate'"));
    }

    [Fact]
    public async Task IntegerWeightAndTypedDimensionsJsonKeepTheirExistingContracts()
    {
        await using var h = await ProductHarness.Create(fixture);
        await h.App.ExecuteAsync($"INSERT INTO catalog.attribute_definitions (id, product_type_id, code, name, data_type, scope) VALUES ('{Guid.NewGuid()}', '{h.Type}', 'dimensions', 'Dimensions', 'dimensions', 'product')");
        const string original = "1.0000000000000000000000000000001";
        var csv = EncodeCsv(
        [
            ["product_slug", "name", "product_type_code", "weight_g", "p.dimensions", "p.year"],
            ["good", "Good", "p105_teapot", "2147483647", "{\"l\":" + original + ",\"w\":1e-28,\"h\":null}", "2025.00"],
            ["fraction", "Fraction", "p105_teapot", "12.00"],
            ["large", "Large", "p105_teapot", "2147483648"],
            ["zero", "Zero", "p105_teapot", "0"],
            ["bad-json", "Bad JSON", "p105_teapot", "", "{\"other\":1}"],
            ["bad-number", "Bad number", "p105_teapot", "", "", original]
        ]);
        var before = await Fingerprint(h);
        var preview = await Import(h, csv, "numeric.csv", true);
        Assert.Equal(before, await Fingerprint(h));
        var apply = await Import(h, csv, "numeric.csv", false);
        Assert.Equal(1, preview.Summary.Ready);
        Assert.Equal(1, apply.Summary.Committed);
        foreach (var result in new[] { preview, apply })
        {
            Assert.Equal(5, result.Summary.Failed);
            Assert.Equal(int.MaxValue, result.Rows[0].WeightG);
            for (var row = 3; row <= 5; row++) AssertRowError(result.Rows, row, "weight_g", "invalid_weight");
            AssertRowError(result.Rows, 6, "p.dimensions", "invalid_attribute");
            AssertRowError(result.Rows, 7, "p.year", "invalid_attribute");
        }
        Assert.Equal(original, await Scalar(h, "SELECT value_json ->> 'l' FROM catalog.product_attribute_values WHERE value_json IS NOT NULL"));
        Assert.Equal("0.0000000000000000000000000001", await Scalar(h, "SELECT value_json ->> 'w' FROM catalog.product_attribute_values WHERE value_json IS NOT NULL"));
    }

    private static byte[] EncodeCsv(string[][] rows) => Encoding.UTF8.GetBytes(string.Join('\n',
        rows.Select(row => string.Join(',', row.Select(cell => "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")))));

    private static void AssertRowError(IReadOnlyList<TeaPremium.Application.Catalog.ProductImportRow> rows, int row, string column, string code) =>
        Assert.Contains(Assert.Single(rows, x => x.Row == row).Errors, e => e.Column == column && e.Code == code);

    private static string Inline(string address, string value) => $"<c r=\"{address}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";

    private static string Column(int index) => index <= 26
        ? ((char)('A' + index - 1)).ToString(CultureInfo.InvariantCulture)
        : Column((index - 1) / 26) + (char)('A' + (index - 1) % 26);

    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed) { Armed = false; throw new IOException("Fixture: lost commit acknowledgement"); }
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}
