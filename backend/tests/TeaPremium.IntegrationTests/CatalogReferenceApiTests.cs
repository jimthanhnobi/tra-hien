#pragma warning disable CA1707 // Existing spec identifiers in test names.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// P1-03: HTTP + real IAM cookie/CSRF + migrated PG17 + restricted tp_app.
public sealed class CatalogReferenceApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Root = "/api/v1/admin/product-types";
    [Fact]
    public async Task FR_CAT_01_TypesCreateListDetailPatchActivationAndConcurrency()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var type = await CreateType(h);
        Assert.Null(type.SkuPrefix); Assert.Equal(7, type.Id.Version);
        var detail = await h.Client.GetFromJsonAsync<ProductTypeView>(Root + "/" + type.Id); Assert.Equal(type, detail);
        var page = await h.Client.GetFromJsonAsync<CatalogPage<ProductTypeView>>(Root); Assert.Single(page!.Items); Assert.Equal(1, page.TotalItems);
        using var update = await h.MutateAsync(HttpMethod.Patch, Root + "/" + type.Id,
            new { rowVersion = type.RowVersion, name = "Tên owner", skuPrefix = "AM", description = "Nội dung owner", googleProductCategory = "123", sortOrder = 4, isActive = false });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<ProductTypeView>())!;
        Assert.Equal(type.Id, updated.Id); Assert.Equal(type.CreatedAt, updated.CreatedAt); Assert.False(updated.IsActive);
        Assert.Equal("AM", updated.SkuPrefix); Assert.Equal("123", updated.GoogleProductCategory); Assert.Equal(4, updated.SortOrder);
        await Error(h, HttpMethod.Patch, Root + "/" + type.Id, new { rowVersion = type.RowVersion, name = "Stale" }, HttpStatusCode.Conflict, "concurrency_conflict");
        using var clear = await h.MutateAsync(HttpMethod.Patch, Root + "/" + type.Id, new { rowVersion = updated.RowVersion, skuPrefix = (string?)null, isActive = true });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var restored = (await clear.Content.ReadFromJsonAsync<ProductTypeView>())!; Assert.Null(restored.SkuPrefix); Assert.True(restored.IsActive); Assert.Equal("Tên owner", restored.Name);
        await Error(h, HttpMethod.Post, Root, new { code = "test_type", name = "Duplicate" }, HttpStatusCode.Conflict, "product_type_code_exists");
        await using var db = h.OwnerContext(); Assert.False(db.Database.HasPendingModelChanges());
        var audits = await db.AuditLogs.Where(x => x.EntityType == "catalog.product_types").ToArrayAsync();
        Assert.True(audits.Length >= 3); Assert.All(audits, x => Assert.Equal(h.OwnerId, x.ActorId));
        Assert.DoesNotContain("Nội dung owner", string.Join("", audits.Select(x => x.Changes)), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("x", "AM", "Tên")]
    [InlineData("INVALID", "AM", "Tên")]
    [InlineData("valid", "aM", "Tên")]
    [InlineData("valid", "A", "Tên")]
    [InlineData("valid", "ABCDE", "Tên")]
    [InlineData("valid", "AM", " ")]
    [InlineData("valid", "AM", "\u0000")]
    public async Task FR_CAT_01_InvalidTypeWritesNothing(string code, string prefix, string name)
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        await Error(h, HttpMethod.Post, Root, new { code, skuPrefix = prefix, name }, HttpStatusCode.BadRequest, "validation_failed");
        await using var db = h.OwnerContext(); Assert.Empty(await db.Set<ProductType>().ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_AttributesAndOptionsKeepNestedOwnershipAndLocalCodes()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var type = await CreateType(h); var other = await CreateType(h, "other_type");
        var attr = await CreateAttribute(h, type.Id); var otherAttr = await CreateAttribute(h, other.Id);
        var path = Attr(type.Id, attr.Id); var option = await CreateOption(h, type.Id, attr.Id);
        var otherOption = await CreateOption(h, other.Id, otherAttr.Id);
        Assert.NotEqual(attr.Id, otherAttr.Id); Assert.NotEqual(option.Id, otherOption.Id);
        var attrs = await h.Client.GetFromJsonAsync<CatalogPage<AttributeView>>(Root + "/" + type.Id + "/attributes"); Assert.Single(attrs!.Items);
        Assert.Equal(attr, await h.Client.GetFromJsonAsync<AttributeView>(path));
        var options = await h.Client.GetFromJsonAsync<CatalogPage<OptionView>>(path + "/options"); Assert.Single(options!.Items);
        Assert.Equal(option, await h.Client.GetFromJsonAsync<OptionView>(path + "/options/" + option.Id));
        await Error(h, HttpMethod.Post, Root + "/" + type.Id + "/attributes", new AttributeInput("style", "Duplicate", "option"), HttpStatusCode.Conflict, "attribute_code_exists");
        await Error(h, HttpMethod.Post, path + "/options", new OptionInput("same", "Duplicate"), HttpStatusCode.Conflict, "option_code_exists");
        using var edit = await h.MutateAsync(HttpMethod.Patch, path, new { rowVersion = attr.RowVersion, name = "Owner attr", isFilterable = true, isRequiredForPublish = true, isVisible = false, sortOrder = 7, isActive = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode); var edited = (await edit.Content.ReadFromJsonAsync<AttributeView>())!;
        Assert.Equal(attr.Id, edited.Id); Assert.False(edited.IsVisible); Assert.True(edited.IsRequiredForPublish); Assert.False(edited.IsActive);
        using var editOption = await h.MutateAsync(HttpMethod.Patch, path + "/options/" + option.Id, new { rowVersion = option.RowVersion, code = "renamed", label = "Owner option", isActive = false });
        Assert.Equal(HttpStatusCode.OK, editOption.StatusCode); var inactive = (await editOption.Content.ReadFromJsonAsync<OptionView>())!;
        Assert.Equal(option.Id, inactive.Id); Assert.False(inactive.IsActive);
        Assert.NotNull(TypedAttributeValidator.Validate(new(attr.Id, type.Id, "option", "product", true), type.Id, "product", new(OptionId: option.Id), new(inactive.Id, attr.Id, inactive.IsActive)));
        foreach (var wrongPath in new[] { Attr(other.Id, attr.Id), Attr(type.Id, Guid.NewGuid()), Attr(Guid.NewGuid(), attr.Id) })
        {
            using var read = await h.Client.GetAsync(wrongPath); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            await Error(h, HttpMethod.Patch, wrongPath, new { rowVersion = edited.RowVersion, name = "Intrusion" }, HttpStatusCode.NotFound, "not_found");
        }
        foreach (var wrongPath in new[] { path + "/options/" + otherOption.Id, Attr(other.Id, attr.Id) + "/options/" + option.Id, path + "/options/" + Guid.NewGuid() })
        {
            using var read = await h.Client.GetAsync(wrongPath); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            await Error(h, HttpMethod.Patch, wrongPath, new { rowVersion = inactive.RowVersion, label = "Intrusion" }, HttpStatusCode.NotFound, "not_found");
        }
        await Error(h, HttpMethod.Post, Attr(other.Id, attr.Id) + "/options", new OptionInput("code", "Bad"), HttpStatusCode.NotFound, "not_found");
        using var unknownAttributes = await h.Client.GetAsync(Root + "/" + Guid.NewGuid() + "/attributes"); Assert.Equal(HttpStatusCode.NotFound, unknownAttributes.StatusCode);
        using var unknownOptions = await h.Client.GetAsync(Attr(type.Id, Guid.NewGuid()) + "/options"); Assert.Equal(HttpStatusCode.NotFound, unknownOptions.StatusCode);
    }
    [Fact]
    public async Task FR_CAT_01_InvalidAttributeAndOptionMetadataAndUnknownParents()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync(); var type = await CreateType(h);
        var attrs = Root + "/" + type.Id + "/attributes";
        foreach (var input in new[] { new AttributeInput("bad", "Name", "unknown"), new("bad", "Name", "text", Scope: "wrong"),
            new("bad", "Name", "text", Unit: "cm"), new("bad", "Name", "boolean", Scope: "variant", IsVariantAxis: true),
            new("bad", "Name", "option", IsVariantAxis: true), new("UPPER", "Name", "text"), new("code", " ", "text") })
            await Error(h, HttpMethod.Post, attrs, input, HttpStatusCode.BadRequest, "validation_failed");
        using var created = await h.MutateAsync(HttpMethod.Post, attrs, new AttributeInput("text_attr", "Text", "text"));
        var text = (await created.Content.ReadFromJsonAsync<AttributeView>())!;
        await Error(h, HttpMethod.Post, Attr(type.Id, text.Id) + "/options", new OptionInput("x", "X"), HttpStatusCode.Conflict, "attribute_not_option");
        var optionAttr = await CreateAttribute(h, type.Id);
        foreach (var input in new[] { new OptionInput("UPPER", "Label"), new("", "Label"), new("code", " "), new(new string('a', 61), "Label") })
            await Error(h, HttpMethod.Post, Attr(type.Id, optionAttr.Id) + "/options", input, HttpStatusCode.BadRequest, "validation_failed");
        await Error(h, HttpMethod.Post, Root + "/" + Guid.NewGuid() + "/attributes", new AttributeInput("code", "Name", "text"), HttpStatusCode.NotFound, "not_found");
        using var unknown = await h.Client.GetAsync(Root + "/" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var badPage = await h.Client.GetAsync(Root + "?pageSize=101"); Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }
    [Fact]
    public async Task FR_CAT_01_ConcurrentCreatesUseRealUniqueConstraintsAndStableErrors()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => h.MutateAsync(HttpMethod.Post, Root, new ProductTypeInput("race", "Race"))));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        var duplicate = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("product_type_code_exists", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var db = h.OwnerContext(); Assert.Single(await db.Set<ProductType>().ToArrayAsync());
        Assert.Single(await db.AuditLogs.Where(x => x.EntityType == "catalog.product_types").ToArrayAsync());
        foreach (var response in responses) response.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR_CAT_01_ConcurrentNestedCreatesMapDatabaseUniqueness(bool options)
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync(); var type = await CreateType(h);
        var path = Root + "/" + type.Id + "/attributes"; object input = new AttributeInput("race", "Race", "number");
        if (options) { var attr = await CreateAttribute(h, type.Id); path = Attr(type.Id, attr.Id) + "/options"; input = new OptionInput("race", "Race"); }
        var responses = await Task.WhenAll(h.MutateAsync(HttpMethod.Post, path, input), h.MutateAsync(HttpMethod.Post, path, input));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        var duplicate = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(options ? "option_code_exists" : "attribute_code_exists", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        foreach (var response in responses) response.Dispose();
    }
    [Fact]
    public async Task BR_CAT_10_FailedRenameRollsBackMetadataOutboxAndAuditTogether()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync(); await h.ExecuteAsync(CatalogInventoryFixture.Seed);
        await CreateType(h, "taken_code");
        var path = Root + "/00000000-0000-0000-0000-000000000001";
        var type = (await h.Client.GetFromJsonAsync<ProductTypeView>(path))!;
        await using (var before = h.OwnerContext()) Assert.Empty(await before.AuditLogs.Where(x => x.EntityId == type.Id).ToArrayAsync());
        await Error(h, HttpMethod.Patch, path, new { rowVersion = type.RowVersion, code = "taken_code", name = "Never committed" }, HttpStatusCode.Conflict, "product_type_code_exists");
        Assert.Equal(type, await h.Client.GetFromJsonAsync<ProductTypeView>(path));
        await using var db = h.OwnerContext(); Assert.Empty(await db.OutboxMessages.ToArrayAsync()); Assert.Empty(await db.AuditLogs.Where(x => x.EntityId == type.Id).ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_NestedSecurityPoliciesDenyEveryMutationAndGuardBothCsrfMethods()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var type = await CreateType(h); var attr = await CreateAttribute(h, type.Id); var option = await CreateOption(h, type.Id, attr.Id);
        var collections = new[] { Root, Root + "/" + type.Id + "/attributes", Attr(type.Id, attr.Id) + "/options" };
        var details = new[] { Root + "/" + type.Id, Attr(type.Id, attr.Id), Attr(type.Id, attr.Id) + "/options/" + option.Id };
        var readOnly = await Staff(h, "sales"); await h.LoginAsync(readOnly, P009Harness.Password + "-Changed");
        foreach (var path in collections.Concat(details)) { using var read = await h.Client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode); }
        foreach (var path in collections) { using var write = await h.MutateAsync(HttpMethod.Post, path, new { }); Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode); }
        foreach (var path in details) { using var write = await h.MutateAsync(HttpMethod.Patch, path, new { }); Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode); }
        await h.LoginAsync();
        foreach (var path in details)
        {
            using var missing = await h.Client.PatchAsJsonAsync(path, new { }); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("csrf_invalid", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        var noCatalog = await Staff(h, "no_permissions"); await h.LoginAsync(noCatalog, P009Harness.Password + "-Changed");
        foreach (var path in collections.Concat(details)) { using var denied = await h.Client.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
    }
    [Fact]
    public async Task BR_CAT_02_BR_CAT_10_BR_CAT_14_MetadataPreservesSkusAndOutboxRebuildsAllFields()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        await h.ExecuteAsync(CatalogInventoryFixture.Seed);
        await h.ExecuteAsync("""
            INSERT INTO catalog.materials(id,slug,name,kind) VALUES ('00000000-0000-0000-0000-000000000007','tu-ne','Tử nê','clay');
            INSERT INTO catalog.artisans(id,slug,name) VALUES ('00000000-0000-0000-0000-000000000008','nghe-nhan','Nghệ nhân');
            UPDATE catalog.products SET material_id='00000000-0000-0000-0000-000000000007', artisan_id='00000000-0000-0000-0000-000000000008', subtitle='Phụ đề';
            UPDATE catalog.variants SET name='Dung tích ấm';
            """);
        var type = (await h.Client.GetFromJsonAsync<ProductTypeView>(Root + "/00000000-0000-0000-0000-000000000001"))!;
        using var patch = await h.MutateAsync(HttpMethod.Patch, Root + "/" + type.Id, new { rowVersion = type.RowVersion, code = "new_type", skuPrefix = "NEW", name = "Loại ấm mới" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        await using (var db = h.OwnerContext())
        {
            Assert.Equal("AM-XX-0001", (await db.Set<Product>().SingleAsync()).SkuBase);
            Assert.Equal("AM-XX-0001", (await db.Set<Variant>().SingleAsync()).Sku);
            var message = Assert.Single(await db.OutboxMessages.ToArrayAsync()); Assert.Equal(RebuildProductSearchText.Message, message.Type); Assert.Equal("pending", message.Status);
        }
        await using (var scope = h.Factory.Services.CreateAsyncScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        await using (var db = h.OwnerContext())
        {
            var product = await db.Set<Product>().SingleAsync();
            foreach (var word in new[] { "Fixture pot", "Phu de", "AM-XX-0001", "Dung tich am", "Nghe nhan", "Tu ne", "Loai am moi" }) Assert.Contains(word.ToLowerInvariant(), product.SearchText, StringComparison.Ordinal);
            var message = await db.OutboxMessages.SingleAsync(); Assert.Equal("processed", message.Status);
            await using var scope = h.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetServices<IOutboxHandler>().Single(x => x.MessageType == RebuildProductSearchText.Message).HandleAsync(message.Id, message.Payload, default);
            await db.Entry(product).ReloadAsync(); Assert.Contains("loai am moi", product.SearchText, StringComparison.Ordinal);
        }
    }
    [Fact]
    public async Task FR_CAT_01_ExistingValuesAndOptionsBlockInvalidatingSchemaChanges()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync(); await h.ExecuteAsync(CatalogInventoryFixture.Seed);
        await h.ExecuteAsync("INSERT INTO catalog.product_attribute_values(product_id,attribute_id,value_number) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000004',12.34)");
        var path = Root + "/00000000-0000-0000-0000-000000000001/attributes/00000000-0000-0000-0000-000000000004";
        var attr = (await h.Client.GetFromJsonAsync<AttributeView>(path))!;
        foreach (var patch in new object[] { new { rowVersion = attr.RowVersion, dataType = "text" }, new { rowVersion = attr.RowVersion, scope = "variant" } })
            await Error(h, HttpMethod.Patch, path, patch, HttpStatusCode.Conflict, "attribute_in_use");
        await h.ExecuteAsync("UPDATE catalog.attribute_definitions SET scope='variant'; DELETE FROM catalog.product_attribute_values; INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,value_number) VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004',12.34)");
        attr = (await h.Client.GetFromJsonAsync<AttributeView>(path))!;
        await Error(h, HttpMethod.Patch, path, new { rowVersion = attr.RowVersion, isVariantAxis = true }, HttpStatusCode.Conflict, "attribute_in_use");
        var optionAttr = await CreateAttribute(h, attr.ProductTypeId); await CreateOption(h, attr.ProductTypeId, optionAttr.Id);
        await Error(h, HttpMethod.Patch, Attr(attr.ProductTypeId, optionAttr.Id), new { rowVersion = optionAttr.RowVersion, dataType = "text" }, HttpStatusCode.Conflict, "attribute_has_options");
        await using var db = h.OwnerContext(); Assert.Equal(12.34m, (await db.Set<VariantAttributeValue>().SingleAsync()).ValueNumber);
    }
    [Fact]
    public async Task FR_CAT_01_AnonymousAllRoutesAndMutationsWithoutPermissionOrCsrfAreDenied()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var paths = new[] { Root, Root + "/" + Guid.NewGuid(), Root + "/" + Guid.NewGuid() + "/attributes", Attr(Guid.NewGuid(), Guid.NewGuid()), Attr(Guid.NewGuid(), Guid.NewGuid()) + "/options", Attr(Guid.NewGuid(), Guid.NewGuid()) + "/options/" + Guid.NewGuid() };
        foreach (var path in paths) { using var read = await h.Client.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode); }
        using (var post = await h.MutateAsync(HttpMethod.Post, Root, new ProductTypeInput("test", "Test"))) Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        await h.LoginAsync();
        using (var missing = await h.Client.PostAsJsonAsync(Root, new ProductTypeInput("test", "Test"))) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Post, Root) { Content = JsonContent.Create(new ProductTypeInput("test", "Test")) })
        { request.Headers.Add("X-CSRF-TOKEN", "invalid"); using var invalid = await h.Client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        var readOnly = await Staff(h, "sales"); await h.LoginAsync(readOnly, P009Harness.Password + "-Changed");
        using (var read = await h.Client.GetAsync(Root)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var denied = await h.MutateAsync(HttpMethod.Post, Root, new ProductTypeInput("test", "Test"))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await h.LoginAsync(); var noCatalog = await Staff(h, "no_permissions"); await h.LoginAsync(noCatalog, P009Harness.Password + "-Changed");
        using (var denied = await h.Client.GetAsync(Root)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var db = h.OwnerContext(); Assert.Empty(await db.Set<ProductType>().ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_OpenApiExportsTwelveOperationsPartialPatchAndNoDeletes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("CI").UseSetting("ConnectionStrings:Default", ""));
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Where(x => x.Name.StartsWith(Root, StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, paths.Length); Assert.Equal(12, paths.Sum(x => x.Value.EnumerateObject().Count()));
        Assert.All(paths, x => Assert.False(x.Value.TryGetProperty("delete", out _)));
        foreach (var name in new[] { "ProductTypePatch", "AttributePatch", "OptionPatch" })
        {
            var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
            Assert.Equal("rowVersion", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(schema.GetProperty("properties").GetProperty("isActive").TryGetProperty("default", out _));
            Assert.False(schema.GetProperty("properties").GetProperty("sortOrder").TryGetProperty("default", out _));
        }
    }
    private static string Attr(Guid type, Guid attr) => $"{Root}/{type}/attributes/{attr}";
    private static async Task<ProductTypeView> CreateType(P009Harness h, string code = "test_type")
    {
        using var response = await h.MutateAsync(HttpMethod.Post, Root, new ProductTypeInput(code, "Loại thử"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var value = (await response.Content.ReadFromJsonAsync<ProductTypeView>())!;
        Assert.EndsWith(value.Id.ToString(), response.Headers.Location!.ToString(), StringComparison.Ordinal); return value;
    }
    private static async Task<AttributeView> CreateAttribute(P009Harness h, Guid type)
    {
        using var response = await h.MutateAsync(HttpMethod.Post, Root + "/" + type + "/attributes", new AttributeInput("style", "Kiểu", "option"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<AttributeView>())!;
    }
    private static async Task<OptionView> CreateOption(P009Harness h, Guid type, Guid attr)
    {
        using var response = await h.MutateAsync(HttpMethod.Post, Attr(type, attr) + "/options", new OptionInput("same", "Tùy chọn"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<OptionView>())!;
    }
    private static async Task Error(P009Harness h, HttpMethod method, string path, object body, HttpStatusCode status, string code)
    {
        using var response = await h.MutateAsync(method, path, body); Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(code, error.GetProperty("code").GetString()); Assert.True(error.TryGetProperty("traceId", out _));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Npgsql", error.ToString(), StringComparison.Ordinal); Assert.DoesNotContain("constraint", error.ToString(), StringComparison.Ordinal);
    }
    private static async Task<string> Staff(P009Harness h, string role)
    {
        await using var scope = h.Factory.Services.CreateAsyncScope(); var email = role + "@example.test";
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>().Handle(new(email, "Fixture", P009Harness.Password, role == "no_permissions" ? [] : [role], h.OwnerId, h.OwnerStamp), default);
        Assert.True(created.IsSuccess);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>().Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
        return email;
    }
}
