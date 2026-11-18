using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TeaPremium.Application.Settings;

namespace TeaPremium.IntegrationTests;

public sealed class P009SettingsTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task DefaultsAreExactRepeatableAndDoNotInventBusinessData()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await using var db = h.OwnerContext();
        foreach (var item in SettingsCatalog.Defaults)
            Assert.Equal(SettingsJson.Canonical(item.Value), SettingsJson.Canonical((await db.Settings.SingleAsync(x => x.Key == item.Key)).Value));
        Assert.False(await db.Settings.AnyAsync(x => x.Key == "payment.bank" || x.Key.StartsWith("site.") || x.Key.StartsWith("notify.")));
        await h.ExecuteAsync("UPDATE platform.settings SET value='12345' WHERE key='shipping.flat_fee_vnd'");
        var script = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().GenerateScript(options: Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.Idempotent);
        await h.ExecuteAsync(script); await h.ExecuteAsync(script); db.ChangeTracker.Clear();
        Assert.Equal("12345", (await db.Settings.SingleAsync(x => x.Key == "shipping.flat_fee_vnd")).Value);
        // Upgrade an existing platform/IAM DB with operator values, not just a fresh DB.
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("202611170919_StaffIam");
        Assert.Equal(9, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='platform'").SingleAsync());
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal("12345", (await db.Settings.SingleAsync(x => x.Key == "shipping.flat_fee_vnd")).Value);
        Assert.Equal(h.OwnerId, (await db.Users.SingleAsync(x => x.Email == "owner@example.test")).Id);
    }

    [Fact]
    public async Task ApiUpdateIsAuditedAndNoopDoesNotWrite()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var group = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/shipping"))!;
        var values = new Dictionary<string, object?> { ["shipping.flat_fee_vnd"] = 45000, ["shipping.free_threshold_vnd"] = null };
        using var put = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new { version = group.Version, values });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var changed = (await put.Content.ReadFromJsonAsync<SettingsGroup>())!;
        Assert.NotEqual(group.Version, changed.Version);
        await using var db = h.OwnerContext();
        var logs = await db.AuditLogs.Where(x => x.EntityType == "platform.settings").ToArrayAsync();
        Assert.Equal(2, logs.Length);
        var fee = Assert.Single(logs, x => x.EntityKey == "shipping.flat_fee_vnd");
        Assert.Equal(h.OwnerId, fee.ActorId); Assert.Equal("staff", fee.ActorType);
        Assert.Equal("3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d", fee.CorrelationId);
        using var changes = JsonDocument.Parse(fee.Changes);
        Assert.Equal(30000, changes.RootElement.GetProperty("value").GetProperty("before").GetInt32());
        Assert.Equal(45000, changes.RootElement.GetProperty("value").GetProperty("after").GetInt32());
        var timestamp = (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == fee.EntityKey)).UpdatedAt;
        using var noop = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new { version = changed.Version, values });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.EntityType == "platform.settings"));
        Assert.Equal(timestamp, (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == fee.EntityKey)).UpdatedAt);
    }

    [Fact]
    public async Task AuthenticationCsrfUnknownGroupsAndAtomicValidationAreEnforced()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync("/api/v1/admin/settings/shipping")).StatusCode);
        await h.LoginAsync();
        var group = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/shipping"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync("/api/v1/admin/settings/shipping", new { version = group.Version, values = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/admin/settings/not-known")).StatusCode);
        using var bad = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new
        {
            version = group.Version,
            values = new Dictionary<string, object> { ["shipping.flat_fee_vnd"] = 90000, ["orders.cod_requires_call"] = true }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await using var db = h.OwnerContext();
        Assert.Equal("30000", (await db.Settings.SingleAsync(x => x.Key == "shipping.flat_fee_vnd")).Value);
        Assert.False(await db.AuditLogs.AnyAsync(x => x.EntityType == "platform.settings"));
    }

    [Fact]
    public async Task SensitiveValuesAreRedactedAndAuditFailureRollsBack()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var group = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/payment"))!;
        using var put = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/payment", new
        {
            version = group.Version,
            values = new Dictionary<string, object> { ["payment.bank"] = new { bin = "123456", bank_name = "Test bank", account_number = "999123456", account_name = "Private Recipient" } }
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await using var db = h.OwnerContext();
        var audit = await db.AuditLogs.SingleAsync(x => x.EntityKey == "payment.bank");
        Assert.DoesNotContain("999123456", audit.Changes); Assert.DoesNotContain("Private Recipient", audit.Changes); Assert.Contains("[redacted]", audit.Changes);
        var shipping = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/shipping"))!;
        await h.ExecuteAsync("CREATE FUNCTION platform.p009_fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test failure'; END $$; CREATE TRIGGER p009_fail_audit BEFORE INSERT ON platform.audit_logs FOR EACH ROW EXECUTE FUNCTION platform.p009_fail_audit()");
        using var failed = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new { version = shipping.Version, values = new Dictionary<string, object> { ["shipping.flat_fee_vnd"] = 99000 } });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("30000", (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "shipping.flat_fee_vnd")).Value);
    }

    [Fact]
    public async Task ConcurrentUpdatesFromSameVersionProduceOneConflict()
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        var group = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/shipping"))!;
        var a = h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new { version = group.Version, values = new Dictionary<string, object> { ["shipping.flat_fee_vnd"] = 40000 } });
        var b = h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/shipping", new { version = group.Version, values = new Dictionary<string, object> { ["shipping.flat_fee_vnd"] = 50000 } });
        var responses = await Task.WhenAll(a, b);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order().ToArray());
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("content", "site", HttpStatusCode.OK)]
    [InlineData("content", "shipping", HttpStatusCode.Forbidden)]
    [InlineData("sales", "site", HttpStatusCode.Forbidden)]
    [InlineData("sales", "shipping", HttpStatusCode.Forbidden)]
    public async Task PermissionsDoNotBroadenGroups(string role, string group, HttpStatusCode expected)
    {
        await using var h = await P009Harness.CreateAsync(fixture); await h.LoginAsync();
        using var created = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/staff/", new { email = "worker@example.test", displayName = "Worker", initialPassword = P009Harness.Password, roles = new[] { role } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var logout = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/logout", new { });
        await h.LoginAsync("worker@example.test");
        using var change = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/change-password", new { currentPassword = P009Harness.Password, newPassword = P009Harness.Password + "-Changed" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        await h.LoginAsync("worker@example.test", P009Harness.Password + "-Changed");
        Assert.Equal(expected, (await h.Client.GetAsync("/api/v1/admin/settings/" + group)).StatusCode);
        using var put = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/" + group, new { version = new string('a', 64), values = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }
}
