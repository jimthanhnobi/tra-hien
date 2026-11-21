using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Time;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    [Fact]
    public async Task P109RequestSchemasMatchStrictHttpPayloads()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = json.RootElement.GetProperty("paths");
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var createLine = LineSchema(Receipts, "post", false);
        var editLine = LineSchema(Receipts + "/{id}", "patch", true);
        AssertNumber(createLine, "quantity", true, false, 1);
        AssertNumber(createLine, "unitCostVnd", true, false, 0);
        AssertNumber(editLine, "quantity", true, false, 1);
        AssertNumber(editLine, "unitCostVnd", false, true, 0);
        var adjustment = Request(Adjustments, "post");
        AssertNumber(adjustment, "quantityDelta", true, false);
        Assert.Equal(["adjustment", "damage"], adjustment.GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()!).ToArray());
        var counting = Request(Stocktakes + "/{id}/lines/{variantId}", "put");
        AssertNumber(counting, "countedQty", true, false, 0);
        Assert.Equal("string", counting.GetProperty("properties").GetProperty("rowVersion").GetProperty("type").GetString());

        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await h.Create()).Variants);
        var other = Assert.Single((await h.Create("Second request fixture")).Variants);
        using (var omitted = await SendJson(HttpMethod.Post, Receipts, "{}"))
            Assert.Empty((await ReadStockResponse<ReceiptView>(omitted, HttpStatusCode.Created)).Lines);
        using (var empty = await SendJson(HttpMethod.Post, Receipts, """{"lines":[]}"""))
            Assert.Empty((await ReadStockResponse<ReceiptView>(empty, HttpStatusCode.Created)).Lines);
        foreach (var body in new[] { """{"lines":null}""", """{"lines":[null]}""", Lines(v.Id, "\"1\"", "0"),
            Lines(v.Id, "1.5", "0"), Lines(v.Id, "1", "\"10\""), Lines(v.Id, "1", "1.5"), Lines(v.Id, "1", "-1"),
            Lines(v.Id, "1", "null"), Lines(v.Id, "1", null) })
        {
            using var bad = await SendJson(HttpMethod.Post, Receipts, body);
            await Problem(bad, HttpStatusCode.BadRequest, "validation_failed");
        }

        using var created = await SendJson(HttpMethod.Post, Receipts, Lines(v.Id, "1", "120"));
        var draft = await ReadStockResponse<ReceiptView>(created, HttpStatusCode.Created);
        var location = Receipts + "/" + draft.Document.Id;
        var header = $$"""{"rowVersion":"{{draft.Document.RowVersion}}",""";
        foreach (var body in new[] { header + "\"lines\":null}", header + "\"lines\":[null]}",
            header + Lines(v.Id, "\"2\"", "0")[1..], header + Lines(v.Id, "1.2", "0")[1..],
            header + Lines(v.Id, "1", "\"0\"")[1..], header + Lines(other.Id, "1", null)[1..],
            header + Lines(other.Id, "1", "null")[1..] })
        {
            using var bad = await SendJson(HttpMethod.Patch, location, body);
            await Problem(bad, HttpStatusCode.BadRequest, "validation_failed");
        }

        using (var omitted = await h.App.MutateAsync(HttpMethod.Patch, location, new { draft.Document.RowVersion }))
            draft = await ReadStockResponse<ReceiptView>(omitted, HttpStatusCode.OK);
        foreach (var (cost, expected) in new (string?, long)[] { (null, 120), ("null", 120), ("0", 0) })
        {
            using var valid = await SendJson(HttpMethod.Patch, location,
                $$"""{"rowVersion":"{{draft.Document.RowVersion}}",""" + Lines(v.Id, "2", cost)[1..]);
            draft = await ReadStockResponse<ReceiptView>(valid, HttpStatusCode.OK);
            Assert.Equal(expected, Assert.Single(draft.Lines).UnitCostVnd);
        }
        using (var added = await SendJson(HttpMethod.Patch, location,
            $$"""{"rowVersion":"{{draft.Document.RowVersion}}",""" + Lines(other.Id, "2", "10")[1..]))
            draft = await ReadStockResponse<ReceiptView>(added, HttpStatusCode.OK);
        Assert.Equal(other.Id, Assert.Single(draft.Lines).VariantId);
        using (var cleared = await h.App.MutateAsync(HttpMethod.Patch, location, new { draft.Document.RowVersion, lines = Array.Empty<int>() }))
            Assert.Empty((await ReadStockResponse<ReceiptView>(cleared, HttpStatusCode.OK)).Lines);

        foreach (var quantity in new[] { "\"1\"", "1.5", "2", "-1" })
        {
            using var adjusted = await SendJson(HttpMethod.Post, Adjustments,
                $$"""{"variantId":"{{v.Id}}","quantityDelta":{{quantity}},"type":"adjustment","reason":"Fixture adjustment"}""");
            if (quantity is "2" or "-1")
                Assert.Equal(quantity == "2" ? 2 : 1, (await ReadStockResponse<AdjustmentView>(adjusted, HttpStatusCode.OK)).OnHandAfter);
            else
                await Problem(adjusted, HttpStatusCode.BadRequest, "validation_failed");
        }

        var stocktake = await NewStocktake(h);
        var countPath = Stocktakes + "/" + stocktake.Document.Id + "/lines/" + v.Id;
        foreach (var quantity in new[] { "\"0\"", "1.5", "-1", "0" })
        {
            using var counted = await SendJson(HttpMethod.Put, countPath,
                $$"""{"rowVersion":"{{stocktake.Document.RowVersion}}","countedQty":{{quantity}}}""");
            if (quantity == "0")
                Assert.Equal(0, Assert.Single((await ReadStockResponse<StocktakeView>(counted, HttpStatusCode.OK)).Lines).CountedQty);
            else
                await Problem(counted, HttpStatusCode.BadRequest, "validation_failed");
        }

        async Task<HttpResponseMessage> SendJson(HttpMethod method, string path, string body)
        {
            using var document = JsonDocument.Parse(body);
            return await h.App.MutateAsync(method, path, document.RootElement);
        }

        JsonElement Resolve(JsonElement reference)
        {
            var name = reference.GetProperty("$ref").GetString()!.Split('/')[^1];
            var resolved = schemas.GetProperty(name);
            Assert.False(resolved.GetProperty("additionalProperties").GetBoolean());
            return resolved;
        }

        JsonElement Request(string path, string method) => Resolve(paths.GetProperty(path).GetProperty(method)
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));

        JsonElement LineSchema(string path, string method, bool versionRequired)
        {
            var request = Request(path, method);
            Assert.Equal(versionRequired, Required(request, "rowVersion"));
            Assert.False(Required(request, "lines"));
            var lineArray = request.GetProperty("properties").GetProperty("lines");
            Assert.Equal("array", lineArray.GetProperty("type").GetString());
            return Resolve(lineArray.GetProperty("items"));
        }
    }

    [Fact]
    public async Task P109DocumentCodesExpandAndSerializeAcrossVietnamDayBoundary()
    {
        var clock = new DocumentCodeClock(new(2026, 11, 20, 16, 59, 59, TimeSpan.Zero));
        await using var h = await ProductHarness.Create(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var first = await NewReceipt(h);
        Assert.Equal("PN-261120-001", first.Document.Code);
        using (var cancel = await h.App.MutateAsync(HttpMethod.Post, Receipts + "/" + first.Document.Id + "/cancel", new { first.Document.RowVersion }))
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("PN-261120-002", (await NewReceipt(h)).Document.Code);
        Assert.Equal("KK-261120-001", (await NewStocktake(h)).Document.Code);
        await h.App.ExecuteAsync($"""
INSERT INTO inventory.receipts(id,code,created_by) VALUES (gen_random_uuid(),'PN-261120-998','{h.App.OwnerId}');
INSERT INTO inventory.stocktakes(id,code,created_by) VALUES (gen_random_uuid(),'KK-261120-999','{h.App.OwnerId}'),
(gen_random_uuid(),'KK-261120-1000','{h.App.OwnerId}');
""");
        var receipts = await Task.WhenAll(NewReceipt(h), NewReceipt(h));
        Assert.Equal(["PN-261120-1000", "PN-261120-999"], receipts.Select(x => x.Document.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("PN-261120-1001", (await NewReceipt(h)).Document.Code);
        Assert.Equal("KK-261120-1001", (await NewStocktake(h)).Document.Code);

        await h.App.ExecuteAsync("""
CREATE FUNCTION inventory.test_reject_code_commit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'test inventory document rollback';
END $$;
CREATE CONSTRAINT TRIGGER test_code_commit AFTER INSERT ON inventory.receipts
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.test_reject_code_commit();
""");
        using (var failed = await h.App.MutateAsync(HttpMethod.Post, Receipts, new { }))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0L, await Scalar(h, "SELECT count(*) FROM inventory.receipts WHERE code='PN-261120-1002'"));
        await h.App.ExecuteAsync("DROP TRIGGER test_code_commit ON inventory.receipts");
        Assert.Equal("PN-261120-1002", (await NewReceipt(h)).Document.Code);

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        Assert.Equal("PN-261121-001", (await NewReceipt(h)).Document.Code);
        Assert.Equal("KK-261121-001", (await NewStocktake(h)).Document.Code);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    public async Task P109CodeOverflowLeavesDocumentsAndAuditUnchanged(string suffix)
    {
        var clock = new DocumentCodeClock(new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        await using var h = await ProductHarness.Create(fixture, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        await h.App.ExecuteAsync($"INSERT INTO inventory.receipts(id,code,created_by) VALUES (gen_random_uuid(),'PN-261120-{suffix}','{h.App.OwnerId}')");
        var before = await Fingerprint(h);
        using var invalid = await h.App.MutateAsync(HttpMethod.Post, Receipts, new { });
        await Problem(invalid, HttpStatusCode.Conflict, "inventory_document_codes_exhausted");
        Assert.Equal(1L, await Scalar(h, "SELECT count(*) FROM inventory.receipts"));
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal("KK-261120-001", (await NewStocktake(h)).Document.Code);
    }

    private static string Lines(Guid id, string quantity, string? cost) =>
        $$"""{"lines":[{"variantId":"{{id}}","quantity":{{quantity}}{{(cost is null ? "" : ",\"unitCostVnd\":" + cost)}}}]}""";

    private static bool Required(JsonElement schema, string field) =>
        schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(x => x.ValueEquals(field));

    private static void AssertNumber(JsonElement schema, string name, bool required, bool nullable, int? minimum = null)
    {
        Assert.Equal(required, Required(schema, name));
        var prop = schema.GetProperty("properties").GetProperty(name);
        var type = prop.GetProperty("type");
        if (nullable)
            Assert.Equal(["integer", "null"], type.EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal).ToArray());
        else
            Assert.Equal("integer", type.GetString());
        if (minimum is not null) Assert.Equal(minimum, prop.GetProperty("minimum").GetInt32());
    }

    private sealed class DocumentCodeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }
}
