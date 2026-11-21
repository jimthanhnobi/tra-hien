using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Pricing;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class SalePriceApiTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    internal const string Route = "/api/v1/admin/variants";
    internal static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] PriceFields = ["createdAt", "endsAt", "id", "note", "rowVersion", "salePriceVnd", "startsAt"];
    private static readonly string[] AuditActions = ["create", "delete"];

    // BR-PRC-03, FR-PRC-04 and FR-PLT-01.
    [Fact]
    public async Task ProvisionedRuntimeCreatesListsAndDeletesWithAuditAndOutbox()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await PricedProduct(h);
        var variant = Assert.Single(p.Variants);
        p = await h.Patch(p, new { categoryIds = new[] { h.Category } });
        var path = Path(variant.Id);
        var list = (await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!;
        Assert.Empty(list.Items);
        Assert.Equal(variant.RowVersion, list.RowVersion);
        await using (var db = new NpgsqlConnection(h.App.RuntimeConnection))
        {
            await db.OpenAsync();
            await using var probe = new NpgsqlCommand("""
                SELECT current_user = 'tp_app' AND current_setting('server_version_num')::int / 10000 = 17
                   AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls
                   AND NOT has_schema_privilege('pricing', 'CREATE')
                   AND NOT has_table_privilege('platform.audit_logs', 'DELETE')
                FROM pg_roles WHERE rolname = current_user
                """, db);
            Assert.Equal(true, await probe.ExecuteScalarAsync());
        }

        var note = "private fixture note";
        var offset = Start.ToOffset(TimeSpan.FromHours(7));
        using var created = await h.App.MutateAsync(HttpMethod.Post, path,
            new { list.RowVersion, salePriceVnd = 9_999_999_999L, startsAt = offset, endsAt = offset.AddHours(1), note });
        var row = await ReadCreated(created);
        Assert.Equal(path + "/" + row.Id, created.Headers.Location?.OriginalString);
        Assert.Equal(Start.UtcDateTime, row.StartsAt);
        Assert.Equal(DateTimeKind.Utc, row.StartsAt.Kind);
        Assert.Equal(note, row.Note);
        Assert.NotEqual(list.RowVersion, row.RowVersion);
        using var listResponse = await h.App.Client.GetAsync(path);
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var dto = Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(PriceFields, dto.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(9_999_999_999L, dto.GetProperty("salePriceVnd").GetInt64());
        Assert.Equal(row.RowVersion, dto.GetProperty("rowVersion").GetString());

        await using (var db = h.App.OwnerContext())
        {
            Assert.Equal(h.App.OwnerId, (await db.Set<SalePrice>().SingleAsync()).CreatedBy);
        }
        using var removed = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + row.Id, new { row.RowVersion });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!.Items);
        Assert.Equal(p.RowVersion, (await h.Get(p.Id)).RowVersion);
        await using var inspect = h.App.OwnerContext();
        var audit = await inspect.AuditLogs.Where(x => x.EntityType == "pricing.sale_prices" && x.EntityId == row.Id)
            .OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(AuditActions, audit.Select(x => x.Action));
        Assert.All(audit, entry =>
        {
            Assert.Equal(h.App.OwnerId, entry.ActorId);
            Assert.Equal("staff", entry.ActorType);
            Assert.NotEqual(default, entry.OccurredAt);
            Assert.DoesNotContain(note, entry.Changes, StringComparison.Ordinal);
        });
        using var added = JsonDocument.Parse(audit[0].Changes);
        using var deleted = JsonDocument.Parse(audit[1].Changes);
        Assert.Equal(JsonValueKind.Null, added.RootElement.GetProperty("sale_price_vnd").GetProperty("before").ValueKind);
        Assert.Equal(9_999_999_999L, added.RootElement.GetProperty("sale_price_vnd").GetProperty("after").GetInt64());
        Assert.Equal(9_999_999_999L, deleted.RootElement.GetProperty("sale_price_vnd").GetProperty("before").GetInt64());
        Assert.Equal(JsonValueKind.Null, deleted.RootElement.GetProperty("sale_price_vnd").GetProperty("after").ValueKind);
        Assert.Equal(variant.Id, added.RootElement.GetProperty("variant_id").GetProperty("after").GetGuid());
        Assert.Equal(Start.UtcDateTime, added.RootElement.GetProperty("starts_at").GetProperty("after").GetDateTime());
        Assert.Equal(AuditValuePolicy.Redacted, added.RootElement.GetProperty("note").GetProperty("after").GetString());
        var messages = await inspect.OutboxMessages.Where(x => x.Type == StorefrontInvalidate.Message).OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(4, messages.Length);
        Assert.All(messages.TakeLast(2), message =>
        {
            var tags = JsonSerializer.Deserialize<StorefrontInvalidate>(message.Payload)!.Tags;
            Assert.Equal(new[] { "product:" + p.Slug, "catalog", "sitemap", "feed", "category:p105-tea" }, tags);
        });
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task UnsupportedContentTypeReturnsProblemWithoutMutation(string method)
    {
        await using var h = await ProductHarness.Create(fixture);
        var variant = Assert.Single((await PricedProduct(h)).Variants);
        var path = Path(variant.Id);
        using var created = await h.App.MutateAsync(HttpMethod.Post, path, Body(variant));
        var row = await ReadCreated(created);
        var before = await Fingerprint(h);
        var url = method == "POST" ? path : path + "/" + row.Id;
        var body = method == "POST" ? Body(variant, 2, 4) : new { row.RowVersion };
        using var csrf = await h.App.Client.GetAsync("/api/v1/admin/auth/csrf");
        var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await h.App.Client.SendAsync(request);
        await Problem(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
        Assert.Equal(before, await Fingerprint(h));
        using var valid = await h.App.MutateAsync(new HttpMethod(method), url, body);
        Assert.Equal(method == "POST" ? HttpStatusCode.Created : HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Fact]
    public async Task DeleteRejectsAnotherRowsTokenEvenWhenBothPromotionsShareXmin()
    {
        await using var h = await ProductHarness.Create(fixture);
        var variant = Assert.Single((await PricedProduct(h)).Variants);
        var path = Path(variant.Id);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        await using (var db = h.App.OwnerContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO pricing.sale_prices (id, variant_id, sale_price_vnd, starts_at, ends_at, created_by)
                VALUES ({firstId}, {variant.Id}, 500, {Start.UtcDateTime}, {Start.AddHours(1).UtcDateTime}, {h.App.OwnerId}),
                       ({secondId}, {variant.Id}, 500, {Start.AddHours(1).UtcDateTime}, {Start.AddHours(2).UtcDateTime}, {h.App.OwnerId})
                """);
            await tx.CommitAsync();
            var rows = await db.Set<SalePrice>().AsNoTracking().ToArrayAsync();
            Assert.Equal(2, rows.Length);
            Assert.NotEqual(0u, rows[0].Version);
            Assert.Equal(rows[0].Version, rows[1].Version);
        }
        var prices = (await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!.Items;
        var first = Assert.Single(prices, x => x.Id == firstId);
        var second = Assert.Single(prices, x => x.Id == secondId);
        var before = await Fingerprint(h);
        using var rejected = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + first.Id, new { second.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await Problem(rejected, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.NotEqual(first.RowVersion, second.RowVersion);
        Assert.Equal(before, await Fingerprint(h));
        using var removed = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + first.Id, new { first.RowVersion });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(second, Assert.Single((await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!.Items));
    }

    [Fact]
    public async Task WrongVariantAndStaleVersionsLeaveTheScheduleAndSideEffectsIntact()
    {
        await using var h = await ProductHarness.Create(fixture);
        var a = Assert.Single((await PricedProduct(h, "Primary product")).Variants);
        var b = Assert.Single((await PricedProduct(h, "Other product")).Variants);
        var missing = Path(Guid.CreateVersion7());
        using var missingList = await h.App.Client.GetAsync(missing);
        await Problem(missingList, HttpStatusCode.NotFound, "not_found");
        using var missingPost = await h.App.MutateAsync(HttpMethod.Post, missing, Body(a));
        await Problem(missingPost, HttpStatusCode.NotFound, "not_found");

        using var created = await h.App.MutateAsync(HttpMethod.Post, Path(a.Id), Body(a));
        var price = await ReadCreated(created);
        var before = await Fingerprint(h);
        using var wrong = await h.App.MutateAsync(HttpMethod.Delete, Path(b.Id) + "/" + price.Id, new { price.RowVersion });
        await Problem(wrong, HttpStatusCode.NotFound, "not_found");
        using var missingDelete = await h.App.MutateAsync(HttpMethod.Delete, missing + "/" + price.Id, new { price.RowVersion });
        await Problem(missingDelete, HttpStatusCode.NotFound, "not_found");
        using var badDelete = await h.App.MutateAsync(HttpMethod.Delete, Path(a.Id) + "/" + price.Id, new { rowVersion = "invalid" });
        await Problem(badDelete, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(before, await Fingerprint(h));

        await h.App.ExecuteAsync($"UPDATE pricing.sale_prices SET note='fixture updated' WHERE id='{price.Id}'");
        before = await Fingerprint(h);
        using var staleDelete = await h.App.MutateAsync(HttpMethod.Delete, Path(a.Id) + "/" + price.Id, new { price.RowVersion });
        await Problem(staleDelete, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(before, await Fingerprint(h));
        var fresh = Assert.Single((await h.App.Client.GetFromJsonAsync<SalePriceList>(Path(a.Id)))!.Items);
        Assert.NotEqual(price.RowVersion, fresh.RowVersion);
        using var deletion = await h.App.MutateAsync(HttpMethod.Delete, Path(a.Id) + "/" + price.Id, new { fresh.RowVersion });
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
    }

    // BR-PRC-02 and BR-CAT-15.
    [Fact]
    public async Task ValidationRejectsIneligibleVariantsAndMalformedInputs()
    {
        await using var h = await ProductHarness.Create(fixture);
        var eligible = Assert.Single((await PricedProduct(h, "Eligible", 1000)).Variants);
        var missing = Assert.Single((await h.Create("Missing base")).Variants);
        var inactive = Assert.Single((await PricedProduct(h, "Inactive", 1000, "inactive")).Variants);
        var before = await Fingerprint(h);
        foreach (var v in new[] { missing, inactive })
        {
            using var bad = await h.App.MutateAsync(HttpMethod.Post, Path(v.Id), Body(v));
            await Problem(bad, HttpStatusCode.UnprocessableEntity, "variant_not_sellable");
        }
        foreach (var price in new long[] { 0, -1, 1000, 1001, 10_000_000_001L })
        {
            using var bad = await h.App.MutateAsync(HttpMethod.Post, Path(eligible.Id), Body(eligible, price: price));
            await Problem(bad, HttpStatusCode.BadRequest, "validation_failed");
        }
        foreach (var (start, end) in new[]
        {
            ("2026-11-21T08:00:00", "2026-11-21T09:00:00Z"),
            ("2026-11-21T08:00:00Z", "2026-11-21"),
            ("2026-11-21T09:00:00Z", "2026-11-21T08:00:00Z"),
            ("2026-11-21T08:00:00Z", "2026-11-21T15:00:00+07:00"),
            ("2026-11-21T08:00:00.0000001Z", "2026-11-21T08:00:00.0000009Z"),
            ("0001-01-01T00:00:00Z", "2026-11-21T08:00:00Z"),
            ("2026-11-21T08:00:00+25:00", "2026-11-21T09:00:00Z")
        })
        {
            using var bad = await h.App.MutateAsync(HttpMethod.Post, Path(eligible.Id), new
            {
                eligible.RowVersion,
                salePriceVnd = 500,
                startsAt = start,
                endsAt = end
            });
            await Problem(bad, HttpStatusCode.BadRequest, "validation_failed");
        }
        var valid = JsonSerializer.Serialize(Body(eligible));
        foreach (var json in new[]
        {
            valid[..^1] + ",\"createdBy\":\"" + Guid.CreateVersion7() + "\"}",
            valid[..^1] + ",\"note\":\"a\\u0000b\"}",
            valid[..^1] + ",\"salePriceVnd\":400}",
            valid.Replace("\"salePriceVnd\":500", "\"salePriceVnd\":1.5", StringComparison.Ordinal),
            valid.Replace("\"salePriceVnd\":500", "\"salePriceVnd\":\"500\"", StringComparison.Ordinal),
            valid.Replace(eligible.RowVersion, "invalid-token", StringComparison.Ordinal),
            "{}",
            "{"
        })
        {
            using var bad = await RawWithCsrf(h, Path(eligible.Id), json);
            await Problem(bad, HttpStatusCode.BadRequest, "validation_failed");
        }
        Assert.Equal(before, await Fingerprint(h));
    }

    [Fact]
    public async Task SingleVariantCannotOverlapButAdjacencyAndOtherVariantsAreIndependent()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await PricedProduct(h);
        p = await h.Change(p, "/variants", new { priceVnd = 1000 });
        var a = p.Variants[0];
        var b = p.Variants[1];
        var path = Path(a.Id);
        using var first = await h.App.MutateAsync(HttpMethod.Post, path, Body(a));
        var firstRow = await ReadCreated(first);
        using var adjacent = await h.App.MutateAsync(HttpMethod.Post, path, Body(a, 2, 4));
        var adjacentRow = await ReadCreated(adjacent);
        using var independent = await h.App.MutateAsync(HttpMethod.Post, Path(b.Id), Body(b));
        await ReadCreated(independent);
        var before = await Fingerprint(h);
        using var overlap = await h.App.MutateAsync(HttpMethod.Post, path, Body(a, 1, 3));
        await Problem(overlap, HttpStatusCode.Conflict, "sale_price_overlap");
        Assert.Equal(before, await Fingerprint(h));
        Assert.Equal(new[] { firstRow, adjacentRow }, (await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!.Items);
    }

    [Fact]
    public async Task ArchivingKeepsTheScheduleAndItsAdministrativeDelete()
    {
        await using var h = await ProductHarness.Create(fixture);
        var p = await PricedProduct(h);
        var v = Assert.Single(p.Variants);
        var path = Path(v.Id);
        using var created = await h.App.MutateAsync(HttpMethod.Post, path, Body(v));
        var promotion = await ReadCreated(created);
        p = await h.Change(p, "/archive", new { });
        Assert.Equal("archived", p.Status);
        Assert.Equal(promotion, Assert.Single((await h.App.Client.GetFromJsonAsync<SalePriceList>(path))!.Items));
        using var removed = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + promotion.Id, new { promotion.RowVersion });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var archivedVariant = Assert.Single(p.Variants);
        var past = new { archivedVariant.RowVersion, salePriceVnd = 500, startsAt = Start.AddDays(-30), endsAt = Start.AddDays(-29) };
        using var pastCreated = await h.App.MutateAsync(HttpMethod.Post, path, past);
        var pastPromotion = await ReadCreated(pastCreated);
        using var pastRemoved = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + pastPromotion.Id, new { pastPromotion.RowVersion });
        Assert.Equal(HttpStatusCode.NoContent, pastRemoved.StatusCode);
        Assert.Equal("archived", (await h.Get(p.Id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditOrOutboxFailureRollsBackCreateAndDelete(bool failAudit)
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await PricedProduct(h)).Variants);
        var path = Path(v.Id);
        using var created = await h.App.MutateAsync(HttpMethod.Post, path, Body(v));
        var row = await ReadCreated(created);
        var before = await Fingerprint(h);
        var table = failAudit ? "audit_logs" : "outbox_messages";
        await h.App.ExecuteAsync($"""
            CREATE FUNCTION pricing.reject_promotion_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private-fixture-error'; END $$;
            CREATE TRIGGER reject_promotion_fixture BEFORE INSERT ON platform.{table}
            FOR EACH ROW EXECUTE FUNCTION pricing.reject_promotion_fixture();
            """);
        using var post = await h.App.MutateAsync(HttpMethod.Post, path, Body(v, 2, 4));
        using var delete = await h.App.MutateAsync(HttpMethod.Delete, path + "/" + row.Id, new { row.RowVersion });
        foreach (var result in new[] { post, delete })
        {
            await Problem(result, HttpStatusCode.InternalServerError, "internal_error");
            Assert.DoesNotContain("private-fixture-error", await result.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await Fingerprint(h));
    }

    [Fact]
    public async Task AllRoutesRequireReadyPricingStaffAndWritesRequireCsrf()
    {
        await using var h = await ProductHarness.Create(fixture);
        var v = Assert.Single((await PricedProduct(h)).Variants);
        var path = Path(v.Id);
        using var created = await h.App.MutateAsync(HttpMethod.Post, path, Body(v));
        var row = await ReadCreated(created);
        var itemPath = path + "/" + row.Id;
        var before = await Fingerprint(h);
        using var anonymous = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var content = await CreateContent(h);
        foreach (var verb in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            var url = verb == HttpMethod.Delete ? itemPath : path;
            object body = verb == HttpMethod.Delete ? new { row.RowVersion } : Body(v, 2, 4);
            using var noSession = await Send(anonymous, verb, url, body);
            Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
            await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
            using var denied = await Send(h.App.Client, verb, url, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await h.App.LoginAsync();
            if (verb == HttpMethod.Get) continue;
            using var noToken = await Send(h.App.Client, verb, url, body);
            await Problem(noToken, HttpStatusCode.BadRequest, "csrf_invalid");
            using var wrongToken = await Send(h.App.Client, verb, url, body, "incorrect");
            await Problem(wrongToken, HttpStatusCode.BadRequest, "csrf_invalid");
        }

        // Leave the owner authenticated but mark the session as needing the initial password change.
        await h.App.ExecuteAsync($"UPDATE iam.staff_users SET must_change_password=true WHERE id='{h.App.OwnerId}'");
        using var notReady = await h.App.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, notReady.StatusCode);
        Assert.Equal(before, await Fingerprint(h));
    }

    internal static string Path(Guid id) => $"{Route}/{id}/sale-prices";

    internal static Task<ProductData> PricedProduct(ProductHarness h, string name = "Pricing product",
        long price = 10_000_000_000L, string status = "active") => h.Create(initial: new
        {
            name,
            productTypeId = h.Type,
            defaultVariant = new { priceVnd = price, status }
        });

    internal static object Body(VariantData v, int startHour = 0, int endHour = 2, long price = 500) =>
        new { rowVersion = v.RowVersion, salePriceVnd = price, startsAt = Start.AddHours(startHour), endsAt = Start.AddHours(endHour) };

    internal static async Task<SalePriceView> ReadCreated(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SalePriceView>())!;
    }

    internal static Task Problem(HttpResponseMessage response, HttpStatusCode status, string code) =>
        ProductG4FindingTests.Problem(response, status, code);

    internal static async Task<string> Fingerprint(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        return JsonSerializer.Serialize(new
        {
            prices = await db.Set<SalePrice>().OrderBy(x => x.Id).ToArrayAsync(),
            audit = await db.AuditLogs.Where(x => x.EntityType == "pricing.sale_prices").OrderBy(x => x.Id).ToArrayAsync(),
            outbox = await db.OutboxMessages.OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private static async Task<HttpResponseMessage> RawWithCsrf(ProductHarness h, string path, string json)
    {
        using var csrf = await h.App.Client.GetAsync("/api/v1/admin/auth/csrf");
        var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await h.App.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod verb, string path, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(verb, path);
        if (verb != HttpMethod.Get) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> CreateContent(ProductHarness h)
    {
        const string email = "pricing-content@example.test";
        await using var scope = h.App.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateStaff, StaffView>>()
            .Handle(new(email, "Fixture", P009Harness.Password, ["content"], h.App.OwnerId, h.App.OwnerStamp), default);
        Assert.True(created.IsSuccess);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
            .Handle(new(created.Value.Id, P009Harness.Password, P009Harness.Password + "-Changed"), default)).IsSuccess);
        return email;
    }
}
