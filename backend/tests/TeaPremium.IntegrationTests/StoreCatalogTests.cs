using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Pricing;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class StoreCatalogTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private static readonly HashSet<string> ForbiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "onHand", "reserved", "available", "avgCostVnd", "lowStockThreshold", "stock", "movements",
        "storageKey", "searchText", "rowVersion", "createdBy", "costVnd"
    };
    private const string Root = "/api/v1/store/";
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
    }

    [Fact]
    public async Task CatalogFacetCodesRoundTripThroughThePublicQuery()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await Seed(app);
        await app.ExecuteAsync("""
            UPDATE catalog.product_types SET code='_review' WHERE id=md5('type')::uuid;
            UPDATE catalog.attribute_options SET code='_gloss' WHERE id=md5('light')::uuid;
            INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type,scope,is_filterable,is_visible)
            VALUES (md5('texture')::uuid,md5('type')::uuid,'_texture','Texture','option','variant',true,true);
            INSERT INTO catalog.attribute_options(id,attribute_id,code,label)
            VALUES (md5('smooth')::uuid,md5('texture')::uuid,'smooth','Smooth');
            INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,option_id)
            VALUES (md5('v180')::uuid,md5('texture')::uuid,md5('smooth')::uuid);
            """);
        await AssertFacetRoundTrip(app, "type", "_review", 3);
        await AssertFacetRoundTrip(app, "attr._texture", "smooth", 1);
        await AssertFacetRoundTrip(app, "attr.finish", "_gloss", 1);

        await app.ExecuteAsync("""
            UPDATE catalog.product_types SET code=repeat('t',40) WHERE id=md5('type')::uuid;
            UPDATE catalog.attribute_definitions SET code=repeat('x',40) WHERE id=md5('texture')::uuid;
            UPDATE catalog.attribute_options SET code=repeat('o',60) WHERE id=md5('smooth')::uuid;
            """);
        await AssertFacetRoundTrip(app, "type", new string('t', 40), 3);
        await AssertFacetRoundTrip(app, "attr." + new string('x', 40), new string('o', 60), 1);

        await app.ExecuteAsync("""
            UPDATE catalog.product_types SET code='a1' WHERE id=md5('type')::uuid;
            UPDATE catalog.attribute_definitions SET code='_x' WHERE id=md5('texture')::uuid;
            UPDATE catalog.attribute_options SET code='_' WHERE id=md5('smooth')::uuid;
            INSERT INTO catalog.product_types(id,code,name) VALUES (md5('different-type')::uuid,'different','Different');
            INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type,scope,is_filterable,is_visible)
            VALUES (md5('ambiguous-finish')::uuid,md5('different-type')::uuid,'finish','Finish','number','product',true,true);
            """);
        await AssertFacetRoundTrip(app, "type", "a1", 3);
        await AssertFacetRoundTrip(app, "attr._x", "_", 1);
        await InvalidQuery(app, "attr.finish=_gloss");
        Assert.Equal("pot", Assert.Single((await Page(app, "?type=a1&attr.finish=_gloss")).Items).Slug);
        foreach (var query in new[] { "type=a", "type=" + new string('t', 41), "type=Upper", "type=bad-code",
                     "attr.x=_", "attr." + new string('x', 41) + "=_", "attr.Finish=light", "attr.finish=bad-option",
                     "attr.finish=" + new string('o', 61) })
            await InvalidQuery(app, query);
    }

    private static async Task AssertFacetRoundTrip(P009Harness app, string key, string value, int count)
    {
        var original = (await Page(app, "")).Facets.Single(f => f.Key == key && f.Value == value);
        Assert.Equal(count, original.ProductCount);
        var roundTrip = await Page(app, "?" + Uri.EscapeDataString(original.Key) + "=" + Uri.EscapeDataString(original.Value));
        Assert.Equal(count, roundTrip.TotalItems);
        Assert.Equal(original, Assert.Single(roundTrip.Facets, f => f.Key == key && f.Value == value));
        if (count == 1) Assert.Equal("pot", Assert.Single(roundTrip.Items).Slug);
        else Assert.Contains(roundTrip.Items, item => item.Slug == "pot");
    }

    [Fact]
    public async Task MalformedTaxonomyFiltersAreRejectedAndValidUnknownSlugsReturnEmpty()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await Seed(app);
        foreach (var query in new[] { "category=review_root", "collection=bad--slug", "material=bad-",
                     "artisan=bad_", "category=Upper", "artisan=%20chu-ban", "collection=" + new string('a', 81) })
            await InvalidQuery(app, query);

        foreach (var key in new[] { "category", "collection", "material", "artisan" })
        {
            Assert.Empty((await Page(app, "?" + key + "=a")).Items);
            Assert.Empty((await Page(app, "?" + key + "=" + new string('a', 80))).Items);
        }
        Assert.Equal(2, (await Page(app, "?category=tea")).TotalItems);
        Assert.Equal("pot", Assert.Single((await Page(app, "?collection=featured")).Items).Slug);
    }

    private static async Task InvalidQuery(P009Harness app, string query)
    {
        using var response = await app.Client.GetAsync(Root + "products?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PublicCatalogFiltersPricesFacetsAndDisclosesOnlyPublicData()
    {
        var clock = new Clock();
        await using var app = await P009Harness.CreateAsync(fixture, s => s.AddSingleton<IClock>(clock));
        await Seed(app);
        var first = await Page(app, "?category=tea&pageSize=1&sort=price_asc");
        Assert.Equal(2, first.TotalItems);
        Assert.Equal("second", Assert.Single(first.Items).Slug);
        var p = await Page(app, "?category=tea&pageSize=1&page=2&sort=price_asc");
        Assert.Equal(first.Facets, p.Facets);
        Assert.Equal("pot", Assert.Single(p.Items).Slug);
        Assert.Equal(2, first.Facets.Single(f => f.Key == "type").ProductCount);
        Assert.Equal(2, first.Facets.Single(f => f.Key == "category" && f.Value == "tea").ProductCount);
        var contextual = await Page(app, "?category=tea&priceMax=999&sort=price_asc");
        Assert.Collection(contextual.Items, v => Assert.Equal("pot", v.Slug), v => Assert.Equal("second", v.Slug));

        Assert.Empty((await Page(app, "?q=am&attr.capacity=245-245&priceMax=900&inStock=true")).Items);
        var filtered = Assert.Single((await Page(app, "?q=am&attr.capacity=245-245&priceMin=600&priceMax=600")).Items);
        Assert.Equal(new StorePrice(600, 800), filtered.Price);
        Assert.Equal("out_of_stock", filtered.Availability);
        var values = (await Page(app, "?q=am&attr.capacity=245-245")).Facets.Where(x => x.Key == "attr.capacity");
        Assert.Equal(245m, decimal.Parse(Assert.Single(values).Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty((await Page(app, "?q=am&attr.capacity=245-245&attr.finish=light")).Items);
        var scoped = Assert.Single((await Page(app, "?attr.year=2026-2026&attr.capacity=180-180")).Items);
        Assert.Equal("pot", scoped.Slug);
        Assert.Empty((await Page(app, "?attr.year=2025-2025&attr.capacity=180-180")).Items);
        foreach (var q in new[] { "am tu sa chu ban", "chu\u2003bân ấm\u00a0sa", "historical", "%_\\", "%", "_", "\\" })
            Assert.Equal("pot", Assert.Single((await Page(app, "?q=" + Uri.EscapeDataString(q))).Items).Slug);
        Assert.Empty((await Page(app, "?q=am%20second")).Items);

        foreach (var bad in new[] { "page=1&page=2", "attr.capacity=245-180", "attr.hidden=1-2", "attr.finish=unknown", "attr.missing=1-2" })
        {
            using var response = await app.Client.GetAsync(Root + "products?" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var detail = await app.Client.GetAsync(Root + "products/pot");
        Assert.Equal("no-store", detail.Headers.CacheControl?.ToString());
        using var data = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        AssertPublic(data.RootElement);
        Assert.DoesNotContain("historical", data.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", data.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", data.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var product = (await detail.Content.ReadFromJsonAsync<StoreProductDetail>())!;
        Assert.Equal("https://assets.example.test/catalog/pot.jpg", Assert.Single(product.Images).Image.Url);
        Assert.Equal("clay", product.Material?.Slug);
        Assert.Equal("chu-ban", product.Artisan?.Slug);
        Assert.Equal("second", Assert.Single(product.RelatedProducts).Slug);
        Assert.Collection(product.Variants, v => Assert.True(v.LowStock), v => Assert.False(v.LowStock));
        Assert.Collection(product.Variants, v => Assert.Equal("in_stock", v.Availability), v => Assert.Equal("out_of_stock", v.Availability));
        var empty = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/empty"))!;
        Assert.Empty(empty.Variants);
        Assert.True(empty.Seo.Indexable);
        var missingStock = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/secret-pot"))!;
        Assert.Empty(missingStock.Categories);
        Assert.Equal("out_of_stock", Assert.Single(missingStock.Variants).Availability);
        foreach (var path in new[] { "products/draft", "products/archived", "categories/hidden", "categories/secret", "collections/private", "materials/private", "artisans/private" })
        {
            using var missing = await app.Client.GetAsync(Root + path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        foreach (var path in new[] { "navigation", "categories/tea", "collections/featured", "materials", "materials/clay", "artisans", "artisans/chu-ban" })
        {
            using var response = await app.Client.GetAsync(Root + path);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertPublic(json.RootElement);
            Assert.DoesNotContain("private", json.RootElement.GetRawText());
            Assert.DoesNotContain("secret", json.RootElement.GetRawText());
        }
    }

    [Fact]
    public async Task SqlPricesAgreeWithPricingCalculatorAtBoundariesWithoutAdminWrites()
    {
        var clock = new Clock();
        await using var app = await P009Harness.CreateAsync(fixture, s => s.AddSingleton<IClock>(clock));
        await Seed(app);
        var schedule = new[] { new ScheduledPrice(600, Start, Start.AddHours(1)),
            new ScheduledPrice(750, Start.AddHours(1), Start.AddHours(2)) };
        foreach (var instant in new[] { Start.AddTicks(-1), Start, Start.AddHours(1).AddTicks(-1),
                     Start.AddHours(1), Start.AddHours(2).AddTicks(-1), Start.AddHours(2) })
        {
            clock.UtcNow = instant;
            var list = await Page(app, "?q=am&attr.capacity=245-245");
            var actual = Assert.Single(list.Items);
            var expected = PricingCalculator.Calculate(true, 800, schedule, instant)!;
            Assert.Equal(expected.EffectivePriceVnd, actual.Price.EffectivePriceVnd);
            Assert.Equal(expected.EffectivePriceVnd < 800 ? 800L : null, actual.Price.OriginalPriceVnd);
            var detail = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot"))!;
            Assert.Equal(actual.Price, detail.Variants.Single(v => v.Id == actual.RepresentativeVariantId).Price);
        }
        await app.ExecuteAsync("UPDATE catalog.variants SET price_vnd=500 WHERE sku='V245'");
        clock.UtcNow = Start;
        Assert.Equal(new StorePrice(500, null), Assert.Single((await Page(app, "?q=am&attr.capacity=245-245")).Items).Price);
    }

    [Fact]
    public async Task SeoAndStockBoundariesAreIndependent()
    {
        await using var app = await P009Harness.CreateAsync(fixture);
        await Seed(app);
        foreach (var (quantity, low) in new[] { (0, false), (1, true), (3, true), (4, false), (5, false), (6, false) })
        {
            await app.ExecuteAsync($"UPDATE inventory.stock_levels SET on_hand={quantity} WHERE variant_id=(SELECT id FROM catalog.variants WHERE sku='V180')");
            var detail = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot"))!;
            Assert.Equal(low, detail.Variants[0].LowStock);
            Assert.Equal(quantity == 0 ? "out_of_stock" : "in_stock", detail.Variants[0].Availability);
        }
        foreach (var count in new[] { 149, 150, 151, 149 })
        {
            await app.ExecuteAsync($"UPDATE catalog.products SET description_word_count={count} WHERE slug='empty'");
            var detail = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/empty"))!;
            Assert.Equal(count, detail.Seo.DescriptionWordCount);
            Assert.Equal(count >= 150, detail.Seo.Indexable);
        }
        await app.ExecuteAsync("UPDATE catalog.products SET description_word_count=151,robots_noindex=true WHERE slug='empty'");
        Assert.False((await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/empty"))!.Seo.Indexable);
        app.Configuration["SITE_INDEXABLE"] = "false";
        Assert.False((await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot"))!.Seo.Indexable);
    }

    [Fact]
    public async Task DetailKeepsPriceAndInventoryOnOneReadSnapshot()
    {
        var probe = new QueryProbe();
        await using var app = await P009Harness.CreateAsync(fixture,
            services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(probe)));
        await Seed(app);
        probe.BeforeNextVariantRead = () => app.ExecuteAsync("""
            UPDATE catalog.variants SET price_vnd=1200 WHERE sku='V180';
            UPDATE inventory.stock_levels SET on_hand=0 WHERE variant_id=md5('v180')::uuid;
            """);
        var initial = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot"))!.Variants[0];
        Assert.Equal(1000, initial.Price.EffectivePriceVnd);
        Assert.True(initial.LowStock);
        var updated = (await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot"))!.Variants[0];
        Assert.Equal(1200, updated.Price.EffectivePriceVnd);
        Assert.Equal("out_of_stock", updated.Availability);
    }

    [Fact]
    public async Task BenchmarkTwoThousandVariants()
    {
        var probe = new QueryProbe();
        await using var app = await P009Harness.CreateAsync(fixture,
            services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(probe)));
        await Seed(app);
        await app.ExecuteAsync("""
            INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
            SELECT md5('bench'||i)::uuid,md5('type')::uuid,'AM-XX-'||lpad(i::text,4,'0'),'bench-'||i,'Bench '||i,'active',now(),150,'bench'
            FROM generate_series(1,500) i;
            INSERT INTO catalog.variants(id,product_id,sku,price_vnd,status,position)
            SELECT md5(p.slug||j)::uuid,p.id,upper(p.slug)||'-'||j,100000+(j*1000),'active',j
            FROM catalog.products p CROSS JOIN generate_series(1,4) j WHERE p.slug LIKE 'bench-%';
            INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,value_number)
            SELECT v.id,md5('capacity')::uuid,100+v.position*50 FROM catalog.variants v WHERE v.sku LIKE 'BENCH-%';
            INSERT INTO catalog.product_categories(product_id,category_id)
            SELECT id,md5('leaf')::uuid FROM catalog.products WHERE slug LIKE 'bench-%';
            INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved)
            SELECT id,position,0 FROM catalog.variants WHERE sku LIKE 'BENCH-%';
            INSERT INTO catalog.product_media(id,product_id,asset_id,alt_text,is_cover)
            SELECT gen_random_uuid(),id,md5('cover')::uuid,'Cover',true FROM catalog.products WHERE slug LIKE 'bench-%';
            ANALYZE;
            """);
        var evidence = Environment.GetEnvironmentVariable("P114_EVIDENCE");
        foreach (var (label, path, budget) in new[]
        {
            ("list20", "products?q=bench", 8),
            ("list100-filtered", "products?q=bench&category=tea&attr.capacity=150-300&priceMin=101000&inStock=true&sort=price_asc&pageSize=100&page=4", 8),
            ("detail", "products/bench-1", 8),
            ("taxonomy", "categories/tea", 4)
        })
        {
            using (var warm = await app.Client.GetAsync(Root + path)) warm.EnsureSuccessStatusCode();
            var samples = new List<double>();
            var commands = 0;
            for (var i = 0; i < 20; i++)
            {
                probe.Commands.Clear();
                var before = Stopwatch.GetTimestamp();
                using var response = await app.Client.GetAsync(Root + path);
                response.EnsureSuccessStatusCode();
                _ = await response.Content.ReadAsByteArrayAsync();
                samples.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                commands = Math.Max(commands, probe.Commands.Count);
            }
            Assert.InRange(commands, 1, budget);
            var p95 = samples.Order().ElementAt(18);
            if (evidence is null) continue;
            await using var connection = new NpgsqlConnection(app.RuntimeConnection);
            await connection.OpenAsync();
            var lines = new List<string> { $"{label}: SQL commands={commands}, HTTP p95={p95:F2} ms (20 samples after warmup)" };
            foreach (var query in probe.Commands)
            {
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + query.Sql, connection);
                foreach (var parameter in query.Parameters) explain.Parameters.Add(parameter.Clone());
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            }
            await File.WriteAllLinesAsync(Path.Combine(evidence, label + ".txt"), lines);
        }
    }

    private sealed class QueryProbe : DbCommandInterceptor
    {
        public Func<Task>? BeforeNextVariantRead { get; set; }

        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            if (BeforeNextVariantRead is { } before && command.CommandText.Contains("catalog.variants", StringComparison.Ordinal))
            {
                BeforeNextVariantRead = null;
                await before();
            }
            return result;
        }
    }

    private static async Task<StoreProductPage> Page(P009Harness app, string query)
    {
        using var response = await app.Client.GetAsync(Root + "products" + query);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertPublic(doc.RootElement);
        return (await response.Content.ReadFromJsonAsync<StoreProductPage>())!;
    }

    private static void AssertPublic(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertPublic(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var field in value.EnumerateObject())
        {
            Assert.DoesNotContain(field.Name, ForbiddenFields);
            AssertPublic(field.Value);
        }
    }

    private static async Task Seed(P009Harness app)
    {
        app.Configuration["SITE_INDEXABLE"] = "true";
        app.Configuration["MediaStorage:PublicBaseUrl"] = "https://assets.example.test";
        await app.ExecuteAsync("""
            INSERT INTO catalog.product_types(id,code,name) VALUES (md5('type')::uuid,'tea','Tea');
            INSERT INTO catalog.categories(id,parent_id,slug,name,status) VALUES
              (md5('tea')::uuid,NULL,'tea','Tea','visible'),
              (md5('leaf')::uuid,md5('tea')::uuid,'leaf','Leaf','visible'),
              (md5('hidden')::uuid,md5('tea')::uuid,'hidden','Hidden','hidden'),
              (md5('secret')::uuid,md5('hidden')::uuid,'secret','Secret','visible');
            INSERT INTO catalog.collections(id,slug,name,status) VALUES
              (md5('featured')::uuid,'featured','Featured','visible'),(md5('collection-private')::uuid,'private','Private','hidden');
            INSERT INTO catalog.materials(id,slug,name,kind,status) VALUES
              (md5('clay')::uuid,'clay','Tử nê','clay','published'),(md5('material-private')::uuid,'private','Private','clay','draft');
            INSERT INTO catalog.artisans(id,slug,name,status) VALUES
              (md5('chu-ban')::uuid,'chu-ban','Chu Bân','published'),(md5('artisan-private')::uuid,'private','Private','draft');
            INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text,material_id,artisan_id)
            SELECT md5(slug)::uuid,md5('type')::uuid,'AM-XX-'||lpad((1000+row_number() OVER (ORDER BY slug))::text,4,'0'),slug,slug,status,now(),150,public.f_unaccent(search),md5('clay')::uuid,md5('chu-ban')::uuid
            FROM (VALUES ('pot','active','Ấm tử sa Chu Bân historical %_\'),('second','active','second'),('secret-pot','active','secret'),
               ('empty','active','empty'),('draft','draft','draft'),('archived','archived','archived')) p(slug,status,search);
            INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,position,is_unique_piece)
            SELECT md5(sku)::uuid, md5(slug)::uuid, upper(sku),status,price,pos,sku='V245'
            FROM (VALUES ('pot','v180','active',1000,0),('pot','v245','active',800,1),('pot','historical','inactive',1,2),
                ('empty','none','inactive',NULL,0),('second','vsecond','active',900,0),('secret-pot','vsecret','active',700,0)) v(slug,sku,status,price,pos);
            INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd)
            VALUES (md5('v180')::uuid,3,0,333),(md5('v245')::uuid,1,1,444),(md5('vsecond')::uuid,5,1,999);
            INSERT INTO catalog.product_categories(product_id,category_id)
            VALUES (md5('pot')::uuid,md5('leaf')::uuid),(md5('pot')::uuid,md5('tea')::uuid),
                (md5('pot')::uuid,md5('secret')::uuid),(md5('second')::uuid,md5('leaf')::uuid),(md5('secret-pot')::uuid,md5('secret')::uuid);
            INSERT INTO catalog.collection_products(collection_id,product_id) VALUES (md5('featured')::uuid,md5('pot')::uuid);
            INSERT INTO catalog.product_relations(product_id,related_product_id)
            VALUES (md5('pot')::uuid,md5('second')::uuid),(md5('pot')::uuid,md5('draft')::uuid);
            INSERT INTO catalog.attribute_definitions(id,product_type_id,code,name,data_type,scope,is_filterable,is_visible)
            VALUES (md5('capacity')::uuid,md5('type')::uuid,'capacity','Capacity','number','variant',true,true),
                (md5('finish')::uuid,md5('type')::uuid,'finish','Finish','option','variant',true,true),
                (md5('hidden-attr')::uuid,md5('type')::uuid,'hidden','Hidden','number','product',true,false),
                (md5('year')::uuid,md5('type')::uuid,'year','Year','number','product',true,true);
            INSERT INTO catalog.product_attribute_values(product_id,attribute_id,value_number)
            VALUES (md5('pot')::uuid,md5('year')::uuid,2026);
            INSERT INTO catalog.attribute_options(id,attribute_id,code,label)
            VALUES (md5('light')::uuid,md5('finish')::uuid,'light','Light');
            INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,value_number)
            VALUES (md5('v180')::uuid,md5('capacity')::uuid,180),(md5('v245')::uuid,md5('capacity')::uuid,245);
            INSERT INTO catalog.variant_attribute_values(variant_id,attribute_id,option_id)
            VALUES (md5('v180')::uuid,md5('finish')::uuid,md5('light')::uuid);
            INSERT INTO media.assets(id,storage_key,mime_type,byte_size,purpose)
            VALUES (md5('cover')::uuid,'catalog/pot.jpg','image/jpeg',1,'catalog'),
                (md5('evidence')::uuid,'private/evidence.jpg','image/jpeg',1,'evidence'),
                (md5('old')::uuid,'catalog/historical.jpg','image/jpeg',1,'catalog'), (md5('staged')::uuid,'pending/file.jpg','image/jpeg',1,'catalog');
            INSERT INTO catalog.product_media(id,product_id,variant_id,asset_id,alt_text,is_cover)
            VALUES (md5('cover')::uuid,md5('pot')::uuid,NULL,md5('cover')::uuid,'Cover',true),
                (md5('evidence')::uuid,md5('pot')::uuid,NULL,md5('evidence')::uuid,'Evidence',false),
                (md5('old')::uuid,md5('pot')::uuid,md5('historical')::uuid,md5('old')::uuid,'Historical',false), (md5('staged')::uuid,md5('pot')::uuid,NULL,md5('staged')::uuid,'Staging',false);
            """);
        await app.ExecuteAsync($"""
            INSERT INTO pricing.sale_prices(id,variant_id,sale_price_vnd,starts_at,ends_at,created_by)
            VALUES (gen_random_uuid(),md5('v245')::uuid,600,'2026-11-21T08:00:00Z','2026-11-21T09:00:00Z','{app.OwnerId}'),
                (gen_random_uuid(),md5('v245')::uuid,750,'2026-11-21T09:00:00Z','2026-11-21T10:00:00Z','{app.OwnerId}');
            """);
    }

    [Fact]
    public async Task StoreProductsListAndDetailDistinguishSoldFromOutOfStockForUniquePieces()
    {
        var clock = new Clock();
        await using var app = await P009Harness.CreateAsync(fixture, s => s.AddSingleton<IClock>(clock));
        await Seed(app);

        // v245 is unique and currently reserved (on_hand=1, reserved=1, available=0, no sale movement) -> out_of_stock.
        var detailBefore = await app.Client.GetFromJsonAsync<StoreProductDetail>(Root + "products/pot");
        Assert.NotNull(detailBefore);
        Assert.Equal("out_of_stock", detailBefore.Variants.Single(v => v.Sku == "V245").Availability);

        // Make a dedicated single-variant unique product to test list card + detail parity.
        await app.ExecuteAsync("""
            INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name,status,published_at,description_word_count,search_text)
            VALUES (md5('solo-pot')::uuid,md5('type')::uuid,'AM-XX-9001','solo-pot','Solo Pot','active',now(),150,'solo pot');
            INSERT INTO catalog.variants(id,product_id,sku,status,price_vnd,position,is_unique_piece)
            VALUES (md5('vsolo')::uuid,md5('solo-pot')::uuid,'VSOLO','active',2500000,0,true);
            INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd)
            VALUES (md5('vsolo')::uuid,0,0,1200000);
            INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,ref_type,ref_id,actor_type,occurred_at)
            VALUES (gen_random_uuid(),md5('vsolo')::uuid,'sale',-1,1200000,0,'order',gen_random_uuid(),'system',now());
            """);

        using var listResponse = await app.Client.GetAsync(Root + "products?q=solo");
        listResponse.EnsureSuccessStatusCode();
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        using var listDoc = JsonDocument.Parse(listRaw);
        AssertPublic(listDoc.RootElement);
        var listPage = JsonSerializer.Deserialize<StoreProductPage>(listRaw, JsonSerializerOptions.Web)!;
        Assert.Equal("sold", Assert.Single(listPage.Items).Availability);

        using var detailResponse = await app.Client.GetAsync(Root + "products/solo-pot");
        detailResponse.EnsureSuccessStatusCode();
        var detailRaw = await detailResponse.Content.ReadAsStringAsync();
        using var detailDoc = JsonDocument.Parse(detailRaw);
        AssertPublic(detailDoc.RootElement);
        var detail = JsonSerializer.Deserialize<StoreProductDetail>(detailRaw, JsonSerializerOptions.Web)!;
        Assert.Equal("sold", Assert.Single(detail.Variants).Availability);
    }
}
