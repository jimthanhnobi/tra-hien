using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-03 / FR-CAT-01, NFR-09: G4 fixes through real cookie/CSRF + restricted tp_app + PG17.
public sealed class CatalogReferenceG4FixTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/product-types";
    private static readonly Guid TypeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid AttributeId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static string AttributePath => $"{Root}/{TypeId}/attributes/{AttributeId}";

    [Theory]
    [InlineData("product", "ml", "l")]
    [InlineData("variant", "ml", "l")]
    [InlineData("product", null, "ml")]
    [InlineData("variant", null, "ml")]
    [InlineData("product", "ml", null)]
    [InlineData("variant", "ml", null)]
    public async Task ExistingValuesRejectEveryEffectiveUnitChangeWithoutSideEffects(string scope, string? before, string? after)
    {
        await using var h = await Setup(scope, before, true);
        var current = (await h.Client.GetFromJsonAsync<AttributeView>(AttributePath))!;
        var counts = await Counts(h);
        using (var response = await h.MutateAsync(HttpMethod.Patch, AttributePath,
            new { rowVersion = current.RowVersion, unit = after, name = "Không được lưu" }))
            await Problem(response, HttpStatusCode.Conflict, "attribute_in_use");
        Assert.Equal(current, await h.Client.GetFromJsonAsync<AttributeView>(AttributePath));
        Assert.Equal(counts, await Counts(h));
        await AssertValue(h, scope);

        // Same unit and unrelated metadata remain editable; successful edits still invalidate old tokens.
        using var same = await h.MutateAsync(HttpMethod.Patch, AttributePath,
            new { rowVersion = current.RowVersion, unit = before, name = "Dung tích đã sửa", isFilterable = true });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var updated = (await same.Content.ReadFromJsonAsync<AttributeView>())!;
        Assert.Equal(before, updated.Unit); Assert.True(updated.IsFilterable);
        using var stale = await h.MutateAsync(HttpMethod.Patch, AttributePath,
            new { rowVersion = current.RowVersion, name = "Token cũ" });
        await Problem(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        await AssertValue(h, scope);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("variant")]
    public async Task NoValuesPermitValidUnitChangesIncludingNull(string scope)
    {
        await using var h = await Setup(scope, "ml", false);
        foreach (var unit in new string?[] { "l", null, "ml" })
        {
            var current = (await h.Client.GetFromJsonAsync<AttributeView>(AttributePath))!;
            using var response = await h.MutateAsync(HttpMethod.Patch, AttributePath, new { rowVersion = current.RowVersion, unit });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(unit, (await response.Content.ReadFromJsonAsync<AttributeView>())!.Unit);
        }
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Set<ProductAttributeValue>().ToArrayAsync());
        Assert.Empty(await db.Set<VariantAttributeValue>().ToArrayAsync());
    }

    [Theory]
    [InlineData("product")]
    [InlineData("variant")]
    public async Task PendingValueInsertBlocksMetadataCheckAndCommittedValuePreventsUnitChange(string scope)
    {
        await using var h = await Setup(scope, "ml", false);
        var current = (await h.Client.GetFromJsonAsync<AttributeView>(AttributePath))!;
        var counts = await Counts(h);
        await using var writer = new NpgsqlConnection(h.RuntimeConnection); await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var committed = false;
        await using (var insert = new NpgsqlCommand(ValueInsert(scope), writer, transaction)) await insert.ExecuteNonQueryAsync();
        var pendingPatch = h.MutateAsync(HttpMethod.Patch, AttributePath, new { rowVersion = current.RowVersion, unit = "l" });
        try
        {
            await WaitForMetadataLock(h, scope);
            Assert.False(pendingPatch.IsCompleted);
            await transaction.CommitAsync();
            committed = true;
            using var response = await pendingPatch.WaitAsync(TimeSpan.FromSeconds(15));
            await Problem(response, HttpStatusCode.Conflict, "attribute_in_use");
            Assert.Equal(current, await h.Client.GetFromJsonAsync<AttributeView>(AttributePath));
            Assert.Equal(counts, await Counts(h));
            await AssertValue(h, scope);
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            // Never leave an HTTP task waiting on a test-owned lock if an assertion fails.
            if (!pendingPatch.IsCompleted) { using var released = await pendingPatch.WaitAsync(TimeSpan.FromSeconds(15)); }
        }
    }

    [Theory]
    [InlineData("type", "code")]
    [InlineData("type", "skuPrefix")]
    [InlineData("type", "name")]
    [InlineData("type", "description")]
    [InlineData("type", "googleProductCategory")]
    [InlineData("attribute", "code")]
    [InlineData("attribute", "name")]
    [InlineData("attribute", "dataType")]
    [InlineData("attribute", "unit")]
    [InlineData("attribute", "scope")]
    [InlineData("option", "code")]
    [InlineData("option", "label")]
    public async Task PostAndPatchRejectNulBeforePersistence(string resource, string field)
    {
        await using var h = await Setup("product", "ml", false);
        var collection = Root;
        object valid = new ProductTypeInput("another_type", "Ấm tử sa");
        string detail;
        if (resource == "attribute")
        {
            collection = $"{Root}/{TypeId}/attributes";
            valid = new AttributeInput("another_capacity", "Dung tích", "number", "ml");
        }
        else if (resource == "option")
        {
            using var attr = await h.MutateAsync(HttpMethod.Post, $"{Root}/{TypeId}/attributes", new AttributeInput("filter_type", "Kiểu lọc", "option"));
            Assert.Equal(HttpStatusCode.Created, attr.StatusCode);
            collection = $"{Root}/{TypeId}/attributes/{(await attr.Content.ReadFromJsonAsync<AttributeView>())!.Id}/options";
            valid = new OptionInput("luoi_loc", "Lưới lọc");
        }
        using (var create = await h.MutateAsync(HttpMethod.Post, collection, valid))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            detail = create.Headers.Location!.ToString();
        }
        var current = await h.Client.GetFromJsonAsync<JsonElement>(detail);
        var counts = await Counts(h);
        var invalid = JsonSerializer.SerializeToNode(valid, JsonSerializerOptions.Web)!.AsObject();
        invalid[field] = "Việt\0Nam";
        using (var post = await h.MutateAsync(HttpMethod.Post, collection, invalid)) await Problem(post, HttpStatusCode.BadRequest, "validation_failed");
        var patch = new JsonObject { ["rowVersion"] = current.GetProperty("rowVersion").GetString(), [field] = "Việt\0Nam" };
        using (var response = await h.MutateAsync(HttpMethod.Patch, detail, patch)) await Problem(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(counts, await Counts(h));
        Assert.Equal(current.GetRawText(), (await h.Client.GetFromJsonAsync<JsonElement>(detail)).GetRawText());
    }

    [Fact]
    public async Task ValidVietnameseWhitespaceLiteralEscapesAndOptionalNullsRoundTrip()
    {
        await using var h = await Setup("product", null, false);
        const string text = "Ấm tử sa\tthủ công\nchứng nhận \\u0000";
        using var created = await h.MutateAsync(HttpMethod.Post, Root,
            new ProductTypeInput("unicode_type", "Ấm tử sa", Description: text, GoogleProductCategory: "Gốm sứ"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var type = (await created.Content.ReadFromJsonAsync<ProductTypeView>())!;
        Assert.Equal(text, type.Description);
        using var clear = await h.MutateAsync(HttpMethod.Patch, $"{Root}/{type.Id}",
            new { rowVersion = type.RowVersion, description = (string?)null, googleProductCategory = (string?)null, skuPrefix = (string?)null });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var result = (await clear.Content.ReadFromJsonAsync<ProductTypeView>())!;
        Assert.Null(result.Description); Assert.Null(result.GoogleProductCategory); Assert.Null(result.SkuPrefix);
        using var unit = await h.MutateAsync(HttpMethod.Patch, AttributePath,
            new { rowVersion = (await h.Client.GetFromJsonAsync<AttributeView>(AttributePath))!.RowVersion, unit = "mi-li-lít\t(mℓ)" });
        Assert.Equal(HttpStatusCode.OK, unit.StatusCode);
        using var attr = await h.MutateAsync(HttpMethod.Post, $"{Root}/{type.Id}/attributes", new AttributeInput("filter_type", "Kiểu lọc", "option"));
        var attribute = (await attr.Content.ReadFromJsonAsync<AttributeView>())!;
        using var option = await h.MutateAsync(HttpMethod.Post, $"{Root}/{type.Id}/attributes/{attribute.Id}/options", new OptionInput("luoi_loc", "Lưới lọc\n7 lỗ"));
        Assert.Equal(HttpStatusCode.Created, option.StatusCode);
        Assert.Equal("Lưới lọc\n7 lỗ", (await option.Content.ReadFromJsonAsync<OptionView>())!.Label);
    }

    private async Task<P009Harness> Setup(string scope, string? unit, bool withValue)
    {
        var h = await P009Harness.CreateAsync(fixture);
        try
        {
            await h.LoginAsync(); await h.ExecuteAsync(CatalogInventoryFixture.Seed);
            await using var db = h.OwnerContext();
            var attr = await db.Set<AttributeDefinition>().SingleAsync(); attr.Scope = scope; attr.Unit = unit;
            await db.SaveChangesAsync();
            if (withValue) await h.ExecuteAsync(ValueInsert(scope));
            return h;
        }
        catch { await h.DisposeAsync(); throw; }
    }
    private static string ValueInsert(string scope) => scope == "product"
        ? "INSERT INTO catalog.product_attribute_values(product_id,attribute_id,value_number) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000004',180)"
        : "INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,value_number) VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004',180)";
    private static async Task AssertValue(P009Harness h, string scope)
    {
        await using var db = h.OwnerContext();
        if (scope == "product") Assert.Equal(180m, (await db.Set<ProductAttributeValue>().SingleAsync()).ValueNumber);
        else Assert.Equal(180m, (await db.Set<VariantAttributeValue>().SingleAsync()).ValueNumber);
    }
    private static async Task<(long Types, long Attributes, long Options, long Audits, long Outbox)> Counts(P009Harness h)
    {
        await using var db = h.OwnerContext();
        return (await db.Set<ProductType>().LongCountAsync(), await db.Set<AttributeDefinition>().LongCountAsync(),
            await db.Set<AttributeOption>().LongCountAsync(), await db.AuditLogs.LongCountAsync(), await db.OutboxMessages.LongCountAsync());
    }
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, error.GetProperty("code").GetString()); Assert.True(error.TryGetProperty("traceId", out _));
        if (code == "validation_failed") Assert.Equal(JsonValueKind.Object, error.GetProperty("errors").ValueKind);
    }
    private static async Task WaitForMetadataLock(P009Harness h, string scope)
    {
        var table = scope == "product" ? "catalog.product_attribute_values" : "catalog.variant_attribute_values";
        await using var observer = new NpgsqlConnection(h.OwnerConnection); await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_locks WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND relation=to_regclass(@table) AND mode='ShareLock' AND NOT granted)", observer);
            command.Parameters.AddWithValue("table", table);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(20, timeout.Token);
        }
    }
}
