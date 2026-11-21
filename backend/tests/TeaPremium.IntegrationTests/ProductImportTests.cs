using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Api.Iam;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Media;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class ProductImportTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Route = ProductApiTests.Root + "/import";
    private const string Headers = "product_slug,name,product_type_code,variant_name,price_vnd,sku,material_slug,description,v.capacity_ml,p.year\n";

    private static readonly string[] ConflictingColumns = ["material_slug", "name", "product_type_code"];
    private static readonly string[] KnownFailure = ["committed", "failed", "committed"];
    private static readonly string[] UnknownFailure = ["committed", "unknown", "notProcessed"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptanceCreatesDraftGroupsAndReportsRowsWithoutDryRunSideEffects(bool xlsx)
    {
        var storage = new FakeMediaStorage { Fail = true };
        await using var h = await ProductHarness.Create(fixture, services => services.AddSingleton<IMediaStorage>(storage));
        await using var db = h.App.OwnerContext();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("170000", (await Scalar(h, "SELECT (current_setting('server_version_num')::int / 10000 * 10000)::text")));
        var before = await Fingerprint(h);
        var csv = Headers + "early,\"Am, tra\",p105_teapot,180ml,1200000,,p105-clay,\"one\n two\",180,2026\n"
            + "minimal,Minimal,p105_teapot\nmissing,Missing,unknown";
        var bytes = xlsx ? Workbook([
            Headers.TrimEnd().Split(','),
            ["early", "Am, tra", "p105_teapot", "180ml", "1200000", "", "p105-clay", "one\n two", "180", "2026"],
            ["minimal", "Minimal", "p105_teapot"],
            ["missing", "Missing", "unknown"]
        ]) : Encoding.UTF8.GetBytes("\uFEFF" + csv);
        var filename = xlsx ? "catalog.xlsx" : "catalog.csv";

        var plan = await Import(h, bytes, filename, true);
        Assert.Equal(2, plan.Summary.Ready);
        Assert.Equal("createDraft", plan.Groups[0].Operation);
        Assert.Equal("early", plan.Groups[0].Slug);
        Assert.Null(plan.Groups[0].ProductId);
        Assert.True(plan.Groups[0].Rows[0].GenerateOnApply);
        Assert.Equal("unknown_product_type", Assert.Single(plan.Groups[2].Rows[0].Errors).Code);
        Assert.Equal(before, await Fingerprint(h));
        Assert.Empty(storage.Assets);

        var result = await Import(h, bytes, filename, false);
        Assert.Equal(2, result.Summary.Committed);
        Assert.Equal(1, result.Summary.Failed);
        var minimal = await h.Get(result.Groups[1].ProductId!.Value);
        Assert.Equal("draft", minimal.Status);
        Assert.False(minimal.Indexable);
        Assert.Null(minimal.FirstPublishedAt);
        var variant = Assert.Single(minimal.Variants);
        Assert.Null(variant.PriceVnd);
        Assert.Equal("AM-XX-0002", variant.Sku);
        Assert.Equal(variant.Id, result.Groups[1].Rows[0].VariantId);
        var first = await h.Get(result.Groups[0].ProductId!.Value);
        Assert.Equal("Am, tra", first.Name);
        Assert.Equal(180, Assert.Single(Assert.Single(first.Variants).Attributes).ValueNumber);
        Assert.Equal(2026, Assert.Single(first.Attributes).ValueNumber);
        var search = await h.App.Client.GetFromJsonAsync<CatalogPage<ProductData>>(ProductApiTests.Root + "?q=180ml%20AM-TN-0001");
        Assert.Equal(first.Id, Assert.Single(search!.Items).Id);
        Assert.Equal("2", await Scalar(h, "SELECT count(*)::text FROM platform.outbox_messages WHERE type='StorefrontInvalidate'"));
        Assert.Equal("2", await Scalar(h, $"SELECT count(*)::text FROM platform.audit_logs WHERE entity_type='catalog.products' AND actor_id='{h.App.OwnerId}' AND action='state_change'"));
        Assert.Equal("0", await Scalar(h, "SELECT (SELECT count(*) FROM inventory.stock_levels) + (SELECT count(*) FROM inventory.stock_movements) + (SELECT count(*) FROM pricing.sale_prices) + (SELECT count(*) FROM media.assets)"));
        Assert.Empty(storage.Assets);
        Assert.Empty(storage.Staging);
    }

    [Fact]
    public async Task GroupRowsInheritProductFieldsAndReplayNeverUpdatesExistingVariants()
    {
        await using var h = await ProductHarness.Create(fixture);
        var content = Headers + "parent,Teapot,p105_teapot,Small,100,MANUAL-01,,first description,180,\n"
            + "parent,Teapot,p105_teapot,Large,,,p105-clay,,245,2026\n"
            + ",Teapot,p105_teapot";
        var plan = await Import(h, content, true);
        Assert.Equal(["reimport_can_create_duplicate"], plan.Groups[1].Warnings);
        var applied = await Import(h, content, false);
        var parent = await h.Get(applied.Groups[0].ProductId!.Value);
        Assert.Equal(h.Material, parent.MaterialId);
        Assert.Equal(2, parent.Variants.Count);
        Assert.Equal("MANUAL-01", parent.Variants[0].Sku);
        Assert.EndsWith("-245", parent.Variants[1].Sku, StringComparison.Ordinal);
        Assert.Null(parent.Variants[1].PriceVnd);

        var replay = await Import(h, content.Replace("Small,100", "Small,700", StringComparison.Ordinal), false);
        Assert.Equal(1, replay.Summary.Failed);
        Assert.Equal(1, replay.Summary.Committed);
        Assert.Contains(replay.Groups[0].Rows[0].Errors, e => e.Code == "slug_exists");
        Assert.Contains(replay.Groups[0].Rows[0].Errors, e => e.Code == "sku_exists");
        Assert.Equal(100, (await h.Get(parent.Id)).Variants[0].PriceVnd);
        Assert.Equal("teapot-2", replay.Groups[1].Slug);
    }

    [Fact]
    public async Task ConflictsAndInvalidReferencesOrTypedValuesReportEveryAffectedGroup()
    {
        await using var h = await ProductHarness.Create(fixture);
        var result = await Import(h, Headers + """
parent,Teapot,p105_teapot,,0,DUP-1,p105-clay,,abc
parent,Other name,p105_cup,,,DUP-2,missing,,1.001
other,Other,p105_teapot,,,DUP-1
invalid,Invalid,p105_teapot,,,,missing
bad,Bad,p105_teapot,,,,,,not-a-number
valid,Valid,p105_teapot
""", false);
        Assert.Equal(1, result.Summary.Committed);
        Assert.Equal(4, result.Summary.Failed);
        foreach (var row in result.Groups[0].Rows)
            Assert.Equal(ConflictingColumns, row.Errors.Where(e => e.Code == "conflicting_product_field").Select(e => e.Column).Order().ToArray());
        Assert.Contains(result.Rows[0].Errors, error => error.Code == "invalid_price");
        Assert.Contains(result.Rows[0].Errors, error => error.Code == "duplicate_sku_in_file");
        Assert.Contains(result.Rows[2].Errors, error => error.Code == "duplicate_sku_in_file");
        Assert.Contains(result.Rows[3].Errors, error => error.Code == "unknown_material");
        Assert.Contains(result.Rows[4].Errors, error => error.Code == "invalid_attribute");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterFirstVariantRollsBackWholeGroupAndStopsOnlyOnUnknownOutcome(bool unknown)
    {
        await using var h = await ProductHarness.Create(fixture);
        var code = unknown ? "P0001" : "23514";
        await h.App.ExecuteAsync($"""
CREATE FUNCTION catalog.reject_variant_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.name = 'Fail' AND EXISTS (SELECT FROM catalog.variants WHERE product_id=NEW.product_id) THEN
    RAISE EXCEPTION 'fixture' USING ERRCODE='{code}';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER reject_import_variant BEFORE INSERT ON catalog.variants FOR EACH ROW EXECUTE FUNCTION catalog.reject_variant_fixture();
""");
        var result = await Import(h, Headers + """
before,Before,p105_teapot
incomplete,Incomplete,p105_teapot,First
incomplete,Incomplete,p105_teapot,Fail
after,After,p105_teapot
""", false);
        Assert.Equal(unknown ? UnknownFailure : KnownFailure,
            result.Groups.Select(g => g.Status).ToArray());
        Assert.Equal("0", await Scalar(h, "SELECT count(*)::text FROM catalog.products WHERE slug='incomplete'"));
        Assert.Equal(unknown ? "1" : "2", await Scalar(h, "SELECT count(*)::text FROM catalog.variants"));
        Assert.Equal(unknown ? "1" : "2", await Scalar(h, "SELECT count(*)::text FROM platform.outbox_messages WHERE type='StorefrontInvalidate'"));
        Assert.Equal(unknown ? "2" : "3", await Scalar(h, "SELECT last_value::text FROM catalog.sku_number_seq"));
    }

    [Fact]
    public async Task ConcurrentImportsSerializeSlugAndReserveManualSkusAcrossGroupsAtSequenceBoundary()
    {
        await using var h = await ProductHarness.Create(fixture);
        await h.App.ExecuteAsync("SELECT setval('catalog.sku_number_seq',9998,true)");
        var result = await Import(h, Headers + """
first,First,p105_teapot
second,Second,p105_teapot,,,AM-XX-9999
""", false);
        Assert.Equal(2, result.Summary.Committed);
        Assert.Equal("AM-XX-9999-V2", result.Rows[0].Sku);
        Assert.Equal("AM-XX-9999", result.Rows[1].Sku);
        Assert.Equal("AM-XX-10000", (await h.Get(result.Groups[1].ProductId!.Value)).SkuBase);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Import(h, Headers + (i < 4 ? "same,Same,p105_teapot" : $"item-{i},Item {i},p105_teapot"), false)));
        Assert.Equal(5, concurrent.Sum(x => x.Summary.Committed));
        Assert.Equal(3, concurrent.Sum(x => x.Summary.Failed));
        Assert.Equal("7", await Scalar(h, "SELECT count(DISTINCT sku)::text FROM catalog.variants"));
        Assert.Equal("10005", await Scalar(h, "SELECT last_value::text FROM catalog.sku_number_seq"));
    }

    [Fact]
    public async Task ApplyRevalidatesCurrentMetadataAndExistingSku()
    {
        await using var h = await ProductHarness.Create(fixture);
        const string data = Headers + "target,Target,p105_teapot,,,MAN-01,p105-clay,,180";
        Assert.Equal(1, (await Import(h, data, true)).Summary.Ready);
        await h.App.ExecuteAsync($"UPDATE catalog.attribute_definitions SET is_active=false WHERE id='{h.VariantAttribute}'");
        Assert.Contains((await Import(h, data, false)).Rows[0].Errors, e => e.Code == "invalid_attribute");
        await h.App.ExecuteAsync($"UPDATE catalog.attribute_definitions SET is_active=true WHERE id='{h.VariantAttribute}'");
        Assert.Equal(1, (await Import(h, data, false)).Summary.Committed);
        Assert.Contains((await Import(h, data.Replace("target,", "different,", StringComparison.Ordinal), false)).Rows[0].Errors, e => e.Code == "sku_exists");
    }

    [Fact]
    public async Task RouteRequiresReadyCatalogStaffCsrfPricingAndOwnerPermissions()
    {
        await using var h = await ProductHarness.Create(fixture);
        using var anonymous = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var ready = await CreateStaff(h, "content", true);
        var unready = await CreateStaff(h, "content", false);
        var readOnly = await CreateStaff(h, "sales", true);
        var data = Encoding.UTF8.GetBytes(Headers + "target,Target,p105_teapot,,100,MAN-01");

        foreach (var mode in new[] { "true", "false" })
        {
            using var unauthorized = await Upload(anonymous, data, "file.csv", mode, csrf: false);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            await h.App.LoginAsync();
            using var csrf = await Upload(h.App.Client, data, "file.csv", mode, csrf: false);
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
            Assert.Contains("csrf_invalid", await csrf.Content.ReadAsStringAsync());
            using var badCsrf = await Upload(h.App.Client, data, "file.csv", mode, suppliedToken: "invalid");
            Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
            await h.App.LoginAsync(unready, P009Harness.Password);
            using var notReady = await Upload(h.App.Client, data, "file.csv", mode);
            Assert.Equal(HttpStatusCode.Forbidden, notReady.StatusCode);
            await h.App.LoginAsync(readOnly, P009Harness.Password + "-Changed");
            using var denied = await Upload(h.App.Client, data, "file.csv", mode);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await h.App.LoginAsync(ready, P009Harness.Password + "-Changed");
            var result = await Import(h, data, "file.csv", mode == "true");
            Assert.Equal(["price_vnd", "sku"], result.Rows[0].Errors.Where(e => e.Code == "forbidden").Select(e => e.Column).Order().ToArray());
        }
        Assert.Equal("0", await Scalar(h, "SELECT count(*)::text FROM catalog.products"));
        Assert.Equal(1, (await Import(h, Headers + "target,Target,p105_teapot", false)).Summary.Committed);
    }

    [Fact]
    public async Task RejectsMalformedAndOverLimitCsvAndXlsxBeforeMutations()
    {
        await using var h = await ProductHarness.Create(fixture);
        var before = await Fingerprint(h);
        string[][] worksheet = [["name", "product_type_code"], ["Demo", "p105_teapot"]];
        var invalid = new (byte[] Bytes, string Name, HttpStatusCode Status, string Code)[]
        {
            (Encoding.UTF8.GetBytes("name,name\none,two"), "a.csv", HttpStatusCode.BadRequest, "duplicate_header"),
            (Encoding.UTF8.GetBytes("name,product_type_code,stock\none,two,3"), "a.csv", HttpStatusCode.BadRequest, "invalid_header"),
            (Encoding.UTF8.GetBytes("name,product_type_code\n\"unterminated"), "a.csv", HttpStatusCode.BadRequest, "malformed_csv"),
            (Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("name,product_type_code")).ToArray(), "a.csv", HttpStatusCode.BadRequest, "invalid_utf8"),
            (Encoding.UTF8.GetBytes(Headers + string.Join('\n', Enumerable.Repeat("x,Demo,p105_teapot", 501))), "a.csv", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded"),
            (Encoding.UTF8.GetBytes(string.Join(',', Enumerable.Repeat("name", 101))), "a.csv", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded"),
            (new byte[5 * 1024 * 1024 + 1], "a.csv", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded"),
            ([1, 2, 3], "a.xlsx", HttpStatusCode.BadRequest, "malformed_file"),
            (Workbook(worksheet, sheet: "Other"), "a.xlsx", HttpStatusCode.BadRequest, "missing_sheet"),
            (Workbook(worksheet, formula: true), "a.xlsx", HttpStatusCode.BadRequest, "formula_not_allowed"),
            (Workbook(worksheet, macro: true), "a.xlsx", HttpStatusCode.UnsupportedMediaType, "unsupported_format"),
            (Workbook(worksheet), "a.xlsm", HttpStatusCode.UnsupportedMediaType, "unsupported_format"),
            (Workbook(worksheet, expandedOverflow: true), "a.xlsx", HttpStatusCode.RequestEntityTooLarge, "file_limit_exceeded")
        };
        foreach (var item in invalid)
        {
            using var response = await Upload(h.App.Client, item.Bytes, item.Name, "false");
            Assert.Equal(item.Status, response.StatusCode);
            Assert.Contains(item.Code, await response.Content.ReadAsStringAsync());
        }
        using var missingMode = await Upload(h.App.Client, Encoding.UTF8.GetBytes(Headers), "file.csv", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingMode.StatusCode);
        using var extraPart = await Upload(h.App.Client, Encoding.UTF8.GetBytes(Headers), "file.csv", "false", extra: true);
        Assert.Equal(HttpStatusCode.BadRequest, extraPart.StatusCode);
        Assert.Equal(before, await Fingerprint(h));
    }

    internal static async Task<string> CreateStaff(ProductHarness h, string role, bool ready)
    {
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        var email = $"{role}-{ready}@example.test";
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>()
            .Handle(new(email, "Fixture", P009Harness.Password, [role], h.App.OwnerId, h.App.OwnerStamp), default);
        Assert.True(created.IsSuccess);
        if (ready)
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
                .Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
        return email;
    }

    private static Task<ProductImportResult> Import(ProductHarness h, string csv, bool preview) =>
        Import(h, Encoding.UTF8.GetBytes(csv), "file.csv", preview);

    private static async Task<ProductImportResult> Import(ProductHarness h, byte[] bytes, string filename, bool preview)
    {
        using var response = await Upload(h.App.Client, bytes, filename, preview ? "true" : "false");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductImportResult>())!;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string filename, string? mode,
        bool csrf = true, string? suppliedToken = null, bool extra = false)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(bytes), "file", filename);
        if (extra) body.Add(new StringContent("extra"), "extra");
        using var request = new HttpRequestMessage(HttpMethod.Post, Route + (mode is null ? "" : "?dryRun=" + mode)) { Content = body };
        if (csrf)
        {
            var token = suppliedToken ?? (await client.GetFromJsonAsync<CsrfResponse>("/api/v1/admin/auth/csrf"))!.Token;
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        return await client.SendAsync(request);
    }

    private static Task<string> Fingerprint(ProductHarness h) => Scalar(h, """
SELECT concat_ws('|',(SELECT count(*) FROM catalog.products),(SELECT count(*) FROM catalog.variants),
  (SELECT concat_ws(',',last_value,is_called) FROM catalog.sku_number_seq),(SELECT count(*) FROM platform.audit_logs),
  (SELECT count(*) FROM platform.outbox_messages),(SELECT count(*) FROM inventory.stock_levels),
  (SELECT count(*) FROM inventory.stock_movements),(SELECT count(*) FROM media.assets))
""");

    private static async Task<string> Scalar(ProductHarness h, string sql)
    {
        await using var connection = new NpgsqlConnection(h.App.OwnerConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    internal static byte[] Workbook(string[][] rows, bool formula = false, bool macro = false,
        bool expandedOverflow = false, string sheet = "SanPham", string? worksheetXml = null, string? shared = null, int? numericColumn = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace xml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            string content = macro ? "application/vnd.ms-excel.sheet.macroEnabled.main+xml"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
            Add("[Content_Types].xml", $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"{content}\"/></Types>");
            Add("xl/workbook.xml", $"<workbook xmlns=\"{xml}\" xmlns:r=\"{office}\"><sheets><sheet name=\"{sheet}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            var sharedRelation = shared is null ? "" : $"<Relationship Id=\"rId2\" Type=\"{office}/sharedStrings\" Target=\"sharedStrings.xml\"/>";
            if (shared is not null) Add("xl/sharedStrings.xml", shared);
            Add("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{office}/worksheet\" Target=\"worksheets/sheet1.xml\"/>{sharedRelation}</Relationships>");
            var data = new XElement(xml + "sheetData", rows.Select((line, row) => new XElement(xml + "row", new XAttribute("r", row + 1),
                line.Select((cell, col) => new XElement(xml + "c", new XAttribute("r", $"{(char)('A' + col)}{row + 1}"), new XAttribute("t", row > 0 && col == numericColumn ? "n" : "inlineStr"),
                    row > 0 && col == numericColumn ? new XElement(xml + "v", cell) : formula && row == 1 && col == 0 ? new XElement(xml + "f", "1+1") : new XElement(xml + "is", new XElement(xml + "t", cell)))))));
            if (expandedOverflow)
            {
                using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), Encoding.UTF8);
                writer.Write($"<worksheet xmlns=\"{xml}\"><sheetData/>");
                var chunk = new string(' ', 1024 * 1024);
                for (var i = 0; i < 33; i++) writer.Write(chunk);
                writer.Write("</worksheet>");
            }
            else Add("xl/worksheets/sheet1.xml", worksheetXml ?? new XElement(xml + "worksheet", data).ToString());
            void Add(string path, string content)
            {
                using var stream = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }
}
