#pragma warning disable CA1707 // Existing spec identifiers in test names.
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Api.Catalog;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class CatalogReferenceSeedTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task FR_CAT_01_EmptySeedExactInventoryAndSecondRunNoOp()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await using (var empty = h.OwnerContext()) Assert.Empty(await empty.Set<ProductType>().ToArrayAsync()); // API startup never seeds.
        var first = await Seed(h); Assert.True(first.IsSuccess); Assert.Equal(new(9, 23, 14, 13), first.Value);
        var second = await Seed(h); Assert.True(second.IsSuccess); Assert.Equal(new(0, 0, 0, 0), second.Value);
        await using var db = h.OwnerContext();
        Assert.Equal(9, await db.Set<ProductType>().CountAsync()); Assert.Equal(23, await db.Set<AttributeDefinition>().CountAsync());
        Assert.Equal(14, await db.Set<AttributeOption>().CountAsync()); Assert.Equal(13, await db.Set<Material>().CountAsync());
        var types = await db.Set<ProductType>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal("teapot:AM,cup:CH,pitcher:TO,tea_tray:KH,tea_caddy:HU,vase:BB,incense:XT,wine_set:RU,accessory:PK", string.Join(',', types.Select(x => x.Code + ":" + x.SkuPrefix)));
        var attributes = await db.Set<AttributeDefinition>().ToArrayAsync();
        Assert.Single(attributes, x => x.IsRequiredForPublish); Assert.Equal(2, attributes.Count(x => x.IsVariantAxis)); Assert.Equal(4, attributes.Count(x => x.IsFilterable));
        Assert.Equal(9, attributes.Count(x => x.DataType == "dimensions" && x.Unit == "cm"));
        Assert.All(attributes, x => Assert.True(new AttributeInputValidator().Validate(new AttributeInput(x.Code, x.Name, x.DataType, x.Unit, x.Scope, x.IsVariantAxis, x.IsRequiredForPublish, x.IsFilterable, x.IsVisible, x.SortOrder, x.IsActive)).IsValid));
        var options = await db.Set<AttributeOption>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal("luoi_loc,1_lo,7_lo,9_lo,khac,thu_cong_toan_phan,ban_thu_cong,khuon,khay_nuoc,khay_kho,khay_hut_nuoc,hop,gom,khac", string.Join(',', options.Select(x => x.Code)));
        var materials = await db.Set<Material>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal("TN,LN,DN,CN,HN,HE,GA,TT,GM,SU,DO,GO,DA", string.Join(',', materials.Select(x => x.SkuCode)));
        Assert.All(materials, x => Assert.Equal("draft", x.Status)); Assert.DoesNotContain(materials, x => x.SkuCode == "XX");
        Assert.Empty(await db.Set<Product>().ToArrayAsync()); Assert.Empty(await db.Set<Variant>().ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_SeedPreservesOwnerChangesIncludingRenamedCodes()
    {
        await using var h = await P009Harness.CreateAsync(fixture); Assert.True((await Seed(h)).IsSuccess);
        await h.ExecuteAsync("""
            UPDATE catalog.product_types SET code='owner_teapot',name='Owner type',sku_prefix='OWN',sort_order=42,is_active=false WHERE code='teapot';
            UPDATE catalog.attribute_definitions SET code='owner_filter',name='Owner attr',is_filterable=false,is_visible=false WHERE id='019ead30-0000-7000-8000-000000000102';
            UPDATE catalog.attribute_options SET code='owner_option',label='Owner label',is_active=false WHERE id='019ead30-0000-7000-8000-000000000201';
            UPDATE catalog.materials SET sku_code='TNX',slug='owner-material',name='Owner material',kind='other' WHERE sku_code='TN';
            """);
        Assert.Equal(new(0, 0, 0, 0), (await Seed(h)).Value);
        await using var db = h.OwnerContext();
        var type = await db.Set<ProductType>().SingleAsync(x => x.Id == CatalogReferenceDefaults.Id(1)); Assert.Equal("owner_teapot", type.Code); Assert.Equal("OWN", type.SkuPrefix); Assert.False(type.IsActive); Assert.Equal(42, type.SortOrder);
        var attr = await db.Set<AttributeDefinition>().SingleAsync(x => x.Id == CatalogReferenceDefaults.Id(102)); Assert.Equal("owner_filter", attr.Code); Assert.False(attr.IsVisible); Assert.False(attr.IsFilterable);
        var option = await db.Set<AttributeOption>().SingleAsync(x => x.Id == CatalogReferenceDefaults.Id(201)); Assert.Equal("Owner label", option.Label); Assert.Equal("owner_option", option.Code); Assert.False(option.IsActive);
        var material = await db.Set<Material>().SingleAsync(x => x.Id == CatalogReferenceDefaults.Id(301)); Assert.Equal("TNX", material.SkuCode); Assert.Equal("owner-material", material.Slug); Assert.Equal("other", material.Kind);
        Assert.Equal(9, await db.Set<ProductType>().CountAsync());
    }
    [Fact]
    public async Task FR_CAT_01_PartialManifestSeedAddsOnlyMissingReferences()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await using (var db = h.OwnerContext())
        {
            var existing = CatalogReferenceDefaults.Types()[0]; existing.CreatedAt = existing.UpdatedAt = DateTime.UtcNow; existing.Name = "Owner partial";
            db.Add(existing); await db.SaveChangesAsync();
        }
        var result = await Seed(h); Assert.True(result.IsSuccess); Assert.Equal(new(8, 23, 14, 13), result.Value);
        await using var check = h.OwnerContext(); Assert.Equal("Owner partial", (await check.Set<ProductType>().SingleAsync(x => x.Id == CatalogReferenceDefaults.Id(1))).Name);
    }
    [Theory]
    [InlineData("type")]
    [InlineData("attribute")]
    [InlineData("option")]
    [InlineData("material")]
    [InlineData("wrong_parent")]
    public async Task FR_CAT_01_ConflictingIdentityReturnsStableErrorWithoutPartialCommit(string conflict)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        if (conflict == "type") await h.ExecuteAsync("INSERT INTO catalog.product_types(id,code,name) VALUES ('00000000-0000-0000-0000-000000000011','teapot','External')");
        if (conflict is "attribute" or "option" or "wrong_parent")
        {
            await h.ExecuteAsync("INSERT INTO catalog.product_types(id,code,name) VALUES ('019ead30-0000-7000-8000-000000000001','teapot','Partial')");
            if (conflict == "attribute") await h.ExecuteAsync("INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type) VALUES ('00000000-0000-0000-0000-000000000012','019ead30-0000-7000-8000-000000000001','capacity_ml','External','text')");
            if (conflict == "option")
            {
                await h.ExecuteAsync("INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type) VALUES ('019ead30-0000-7000-8000-000000000102','019ead30-0000-7000-8000-000000000001','filter_type','Partial','option')");
                await h.ExecuteAsync("INSERT INTO catalog.attribute_options(id,attribute_id,code,label) VALUES ('00000000-0000-0000-0000-000000000013','019ead30-0000-7000-8000-000000000102','luoi_loc','External')");
            }
            if (conflict == "wrong_parent") await h.ExecuteAsync("INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type) VALUES ('019ead30-0000-7000-8000-000000000107','019ead30-0000-7000-8000-000000000001','capacity','Wrong parent','number')");
        }
        if (conflict == "material") await h.ExecuteAsync("INSERT INTO catalog.materials(id,sku_code,slug,name,kind) VALUES ('00000000-0000-0000-0000-000000000014','TN','external','External','clay')");
        await using var before = h.OwnerContext(); var types = await before.Set<ProductType>().CountAsync(); var attrs = await before.Set<AttributeDefinition>().CountAsync(); var options = await before.Set<AttributeOption>().CountAsync();
        var result = await Seed(h); Assert.False(result.IsSuccess); Assert.Equal("catalog_seed_identity_conflict", result.Error.Code);
        await using var after = h.OwnerContext(); Assert.Equal(types, await after.Set<ProductType>().CountAsync()); Assert.Equal(attrs, await after.Set<AttributeDefinition>().CountAsync()); Assert.Equal(options, await after.Set<AttributeOption>().CountAsync());
        Assert.Empty(await after.AuditLogs.Where(x => x.EntityType.StartsWith("catalog.")).ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_ConcurrentSeedTransactionsConvergeAndDbFailureRollsBackEverything()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var results = await Task.WhenAll(Seed(h), Seed(h)); Assert.All(results, x => Assert.True(x.IsSuccess));
        Assert.Equal(9, results.Sum(x => x.Value.ProductTypesAdded)); Assert.Equal(23, results.Sum(x => x.Value.AttributesAdded));
        await using var fresh = await P009Harness.CreateAsync(fixture);
        await fresh.ExecuteAsync("""
            CREATE FUNCTION catalog.reject_seed() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test_failure'; END; $$;
            CREATE TRIGGER reject_seed BEFORE INSERT ON catalog.materials FOR EACH ROW EXECUTE FUNCTION catalog.reject_seed();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Seed(fresh));
        await using var db = fresh.OwnerContext(); Assert.Empty(await db.Set<ProductType>().ToArrayAsync()); Assert.Empty(await db.Set<AttributeDefinition>().ToArrayAsync()); Assert.Empty(await db.Set<AttributeOption>().ToArrayAsync()); Assert.Empty(await db.Set<Material>().ToArrayAsync());
        Assert.Empty(await db.AuditLogs.Where(x => x.EntityType.StartsWith("catalog.")).ToArrayAsync());
    }
    [Fact]
    public async Task FR_CAT_01_CliExecutesSeedAndIdempotentRetryWithMeaningfulExitCodes()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var first = await Cli(h, "Test"); Assert.Equal(0, first.Code); Assert.Contains("types=9 attributes=23 options=14 materials=13", first.Output, StringComparison.Ordinal);
        var second = await Cli(h, "Test"); Assert.Equal(0, second.Code); Assert.Contains("types=0 attributes=0 options=0 materials=0", second.Output, StringComparison.Ordinal);
        await h.ExecuteAsync("UPDATE catalog.materials SET sku_code='TNX' WHERE sku_code='TN'; INSERT INTO catalog.materials(id,sku_code,slug,name,kind) VALUES ('00000000-0000-0000-0000-000000000031','TN','conflict','Conflict','clay')");
        var conflict = await Cli(h, "Test"); Assert.Equal(3, conflict.Code); Assert.Contains("catalog_seed_identity_conflict", conflict.Output, StringComparison.Ordinal);
    }
    [Fact]
    public async Task FR_CAT_01_CliRefusesProductionAndConfigurationOverrideBeforeConnecting()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var production = await Cli(h, "Production"); Assert.Equal(2, production.Code); Assert.Contains("catalog_seed_production_hold", production.Output, StringComparison.Ordinal);
        var overridden = await Cli(h, "Production", "--environment=Test"); Assert.Equal(2, overridden.Code); Assert.Contains("catalog_seed_usage", overridden.Output, StringComparison.Ordinal);
        Assert.Equal("catalog_seed_unsafe_target", SeedCatalogReferenceConsole.Preflight(["seed-catalog-reference"], "Test", "Host=remote.invalid;Database=real_catalog;Username=tp_app"));
        Assert.Equal("catalog_seed_unsafe_target", SeedCatalogReferenceConsole.Preflight(["seed-catalog-reference"], "Test", "not a connection"));
        await using var db = h.OwnerContext(); Assert.Empty(await db.Set<ProductType>().ToArrayAsync());
    }
    private static async Task<Result<CatalogSeedOutcome>> Seed(P009Harness h)
    {
        await using var scope = h.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogReferenceSeed>().SeedAsync(default);
    }
    private static async Task<(int Code, string Output)> Cli(P009Harness h, string environment, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("seed-catalog-reference"); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_ENVIRONMENT"] = environment; start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["ConnectionStrings__Default"] = h.RuntimeConnection;
        start.Environment["Iam__KeyCertificatePath"] = h.CertificatePath;
        start.Environment["Iam__KeyCertificatePassword"] = "";
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        return (process.ExitCode, await stdout + await stderr);
    }
}
