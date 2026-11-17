using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

// T01–T27 are the owner's LOCAL test plan labels, not new acceptance criteria.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "Test-local input and expected arrays are intentionally independent.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "Preserve required BR-IAM identifiers in test names.")]
public sealed class StaffIamTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Password = "Test-Only-Password-29!";
    private const string NewPassword = "Changed-Only-Password-47!";
    private const string Auth = "/api/v1/admin/auth";
    private const string Staff = "/api/v1/admin/staff/";

    [Fact]
    public async Task T01T02MigrationAndExactSeedsAreRepeatableAndRollbackPreservesPlatform()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='iam'").SingleAsync());
        Assert.Empty(await db.Users.ToArrayAsync());
        Assert.Equal(new[] { "content", "owner", "sales" }, await db.Roles.OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync());
        var expected = new Dictionary<string, string[]>
        {
            ["owner"] = "audit.read,catalog.publish,catalog.read,catalog.write,content.publish,content.write,costs.read,customers.erase,customers.export,customers.merge,customers.read,customers.write,finance.read,finance.write,inventory.approve,inventory.read,inventory.write,marketing.spend,marketing.write,orders.override_price,orders.read,orders.write,payments.write,pricing.write,refunds.write,returns.write,settings.write,staff.manage".Split(','),
            ["sales"] = "catalog.read,customers.read,customers.write,inventory.read,inventory.write,marketing.write,orders.override_price,orders.read,orders.write,payments.write,returns.write".Split(','),
            ["content"] = "catalog.publish,catalog.read,catalog.write,content.publish,content.write,inventory.read".Split(',')
        };
        foreach (var role in await db.Roles.ToArrayAsync())
            Assert.Equal(expected[role.Name!], await db.RoleClaims.Where(x => x.RoleId == role.Id).OrderBy(x => x.ClaimValue).Select(x => x.ClaimValue).ToArrayAsync());
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(45, await db.RoleClaims.CountAsync());
        await db.GetService<IMigrator>().MigrateAsync("202611161624_InitialPlatform");
        Assert.Equal(9, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='platform'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='iam'").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(45, await db.RoleClaims.CountAsync());
    }

    [Fact]
    public async Task T03T14CookieIsHostOnlySecureAndAnonymousIs401()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
        await h.BootstrapAsync();
        using var response = await Login(h.Client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tp_admin=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(Staff)).StatusCode);
    }

    [Fact]
    public async Task T04T05BR_IAM_01FiveFailuresLockExactly15MinutesAndRevokeSessions()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        using var attacker = h.NewClient();
        for (var i = 0; i < 5; i++)
            await Problem(await Login(attacker, password: "Wrong-Password-29!"), HttpStatusCode.Unauthorized, "invalid_credentials");
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
        await Problem(await Login(attacker), HttpStatusCode.Unauthorized, "invalid_credentials");
        h.Time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        await Problem(await Login(attacker), HttpStatusCode.Unauthorized, "invalid_credentials");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await LoginOk(attacker);
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task T06InvalidAndInactiveUsersHaveSamePublicLoginFailure()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var user = await Create(h.Client, roles: ["sales"]);
        var patch = await Patch(h.Client, user, new { user.RowVersion, isActive = false });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var stranger = h.NewClient();
        using var unknown = await Login(stranger, "absent@example.test");
        using var inactive = await Login(stranger, user.Email);
        await Problem(unknown, HttpStatusCode.Unauthorized, "invalid_credentials");
        await Problem(inactive, HttpStatusCode.Unauthorized, "invalid_credentials");
        using var a = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        using var b = JsonDocument.Parse(await inactive.Content.ReadAsStringAsync());
        Assert.Equal(a.RootElement.GetProperty("title").GetString(), b.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task T04FR_IAM_01FailuresOutsideRollingWindowDoNotAccumulate()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        for (var i = 0; i < 4; i++) await Login(h.Client, password: "wrong");
        h.Time.Advance(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 4; i++)
            await Problem(await Login(h.Client, password: "wrong"), HttpStatusCode.Unauthorized, "invalid_credentials");
        await LoginOk(h.Client);
    }

    [Theory]
    [InlineData("Short-1!")]
    [InlineData("no-uppercase-123!")]
    [InlineData("NO-LOWERCASE-123!")]
    [InlineData("NoDigits-Password!")]
    [InlineData("NoSpecialPassword123")]
    public async Task T07BR_IAM_01RejectWeakPasswords(string password)
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        await Problem(await Mutate(h.Client, HttpMethod.Post, Staff,
            new { email = "weak@example.test", displayName = "Weak", initialPassword = password, roles = new[] { "sales" } }), HttpStatusCode.BadRequest, "validation_failed");
        await using var scope = h.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task T08T09BR_IAM_06FirstPasswordChangeGateAndAllOldSessionsInvalidated()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var user = await Create(h.Client, roles: ["sales"]);
        using var first = h.NewClient();
        using var second = h.NewClient();
        await LoginOk(first, user.Email);
        await LoginOk(second, user.Email);
        await Problem(await first.GetAsync("/api/v1/admin/__iam-test/orders.read"), HttpStatusCode.Forbidden, "forbidden");
        await Problem(await Mutate(first, HttpMethod.Post, Auth + "/totp/setup", new { }), HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(HttpStatusCode.NoContent, (await Mutate(first, HttpMethod.Post, Auth + "/change-password",
            new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        await Problem(await second.GetAsync(Auth + "/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Problem(await Login(first, user.Email), HttpStatusCode.Unauthorized, "invalid_credentials");
        await LoginOk(first, user.Email, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await first.GetAsync("/api/v1/admin/__iam-test/orders.read")).StatusCode);
    }

    [Fact]
    public async Task T10T11T12T13BR_IAM_02ProductionOwnerEnrollmentVerificationReplayAndExpiry()
    {
        await using var h = await Harness.CreateAsync(fixture, production: true);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Forbidden, "forbidden");
        var setup = await (await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/setup", new { })).Content.ReadFromJsonAsync<TotpSetup>();
        Assert.NotNull(setup);
        Assert.Contains("otpauth://totp/", setup.ProvisioningUri, StringComparison.Ordinal);
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await db.Users.SingleAsync()).TwoFactorEnabled);
            Assert.DoesNotContain(setup.SharedKey, string.Join('|', await db.UserTokens.Select(x => x.Value).ToArrayAsync()), StringComparison.Ordinal);
        }
        var valid = Code(setup.SharedKey, h.Time.GetUtcNow());
        var wrong = valid == "000000" ? "000001" : "000000";
        await Problem(await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/verify", new { code = wrong }), HttpStatusCode.BadRequest, "totp_invalid");
        Assert.Equal(HttpStatusCode.NoContent, (await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/verify", new { code = valid })).StatusCode);
        using var fresh = h.NewClient();
        await Problem(await Login(fresh), HttpStatusCode.Unauthorized, "invalid_credentials");
        await Problem(await Login(fresh, code: valid), HttpStatusCode.Unauthorized, "invalid_credentials");
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await Problem(await Login(fresh, code: valid), HttpStatusCode.Unauthorized, "invalid_credentials");
        await LoginOk(fresh, code: Code(setup.SharedKey, h.Time.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(Staff)).StatusCode);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        using var simultaneousA = h.NewClient();
        using var simultaneousB = h.NewClient();
        var sameCode = Code(setup.SharedKey, h.Time.GetUtcNow());
        var attempts = await Task.WhenAll(Login(simultaneousA, code: sameCode), Login(simultaneousB, code: sameCode));
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Unauthorized);
        await Problem(await Mutate(fresh, HttpMethod.Post, Auth + "/totp/setup", new { }), HttpStatusCode.Conflict, "totp_already_enabled");
        Assert.DoesNotContain(setup.SharedKey, h.Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.ProvisioningUri, h.Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(valid, h.Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T10EnrollmentExpiresWithoutActivatingAndCannotBruteforce()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var setup = await (await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/setup", new { })).Content.ReadFromJsonAsync<TotpSetup>();
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Problem(await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/verify", new { code = Code(setup!.SharedKey, h.Time.GetUtcNow()) }), HttpStatusCode.BadRequest, "totp_invalid");
        for (var i = 0; i < 4; i++)
            await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/verify", new { code = "000000" });
        await Problem(await h.Client.GetAsync(Auth + "/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Theory]
    [InlineData("sales", "staff.manage", false)]
    [InlineData("sales", "costs.read", false)]
    [InlineData("sales", "customers.read", true)]
    [InlineData("sales", "catalog.write", false)]
    [InlineData("sales", "inventory.write", true)]
    [InlineData("sales", "orders.read", true)]
    [InlineData("content", "staff.manage", false)]
    [InlineData("content", "costs.read", false)]
    [InlineData("content", "customers.read", false)]
    [InlineData("content", "catalog.write", true)]
    [InlineData("content", "inventory.write", false)]
    [InlineData("content", "orders.read", false)]
    public async Task T15T16T21PermissionsAreEnforcedByActualCookieAndTrustedDatabase(string role, string permission, bool allowed)
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var user = await Create(h.Client, roles: [role]);
        using var employee = h.NewClient();
        await FirstLogin(employee, user.Email);
        employee.DefaultRequestHeaders.Add("X-Permission", "staff.manage,costs.read");
        employee.DefaultRequestHeaders.Add("X-Role", "owner");
        employee.DefaultRequestHeaders.Add("X-Staff-Id", (await Me(h.Client)).User.Id.ToString());
        var response = await employee.GetAsync("/api/v1/admin/__iam-test/" + permission);
        if (allowed) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        else await Problem(response, HttpStatusCode.Forbidden, "forbidden");
        await Problem(await employee.GetAsync(Staff), HttpStatusCode.Forbidden, "forbidden");
        await Problem(await Mutate(employee, HttpMethod.Post, Staff,
            new { email = "escalated@example.test", displayName = "X", initialPassword = Password, roles = new[] { "owner" } }), HttpStatusCode.Forbidden, "forbidden");
        await Problem(await Patch(employee, user, new { user.RowVersion, roles = new[] { "owner" } }), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task T17RolesAndClaimsChangesRevokeOrRefreshOldAuthorizationAndMultipleRolesWork()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var user = await Create(h.Client, roles: ["sales", "content"]);
        using var employee = h.NewClient();
        await FirstLogin(employee, user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await employee.GetAsync("/api/v1/admin/__iam-test/catalog.write")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await employee.GetAsync("/api/v1/admin/__iam-test/orders.read")).StatusCode);
        user = (await (await h.Client.GetAsync(Staff + user.Id)).Content.ReadFromJsonAsync<StaffView>())!;
        Assert.Equal(HttpStatusCode.OK, (await Patch(h.Client, user, new { user.RowVersion, roles = new[] { "content" } })).StatusCode);
        await Problem(await employee.GetAsync(Auth + "/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await LoginOk(employee, user.Email, NewPassword);
        await Problem(await employee.GetAsync("/api/v1/admin/__iam-test/orders.read"), HttpStatusCode.Forbidden, "forbidden");
        // A trusted role-claim change outside an HTTP endpoint is refreshed on EVERY request.
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var content = await db.Roles.SingleAsync(x => x.Name == "content");
        db.RoleClaims.Remove(await db.RoleClaims.SingleAsync(x => x.RoleId == content.Id && x.ClaimValue == "catalog.write"));
        await db.SaveChangesAsync();
        await Problem(await employee.GetAsync("/api/v1/admin/__iam-test/catalog.write"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task T18T19MissingInvalidAndCrossUserCsrfRejectedBeforeEffectsAndLogoutWorks()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        var anonymousToken = (await (await h.Client.GetAsync(Auth + "/csrf")).Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var noCsrf = await h.Client.PostAsJsonAsync(Auth + "/login", new { email = Harness.OwnerEmail, password = Password });
        await Problem(noCsrf, HttpStatusCode.BadRequest, "csrf_invalid");
        await LoginOk(h.Client);
        using (var mismatched = new HttpRequestMessage(HttpMethod.Post, Auth + "/logout") { Content = JsonContent.Create(new { }) })
        {
            mismatched.Headers.Add("X-CSRF-TOKEN", anonymousToken);
            await Problem(await h.Client.SendAsync(mismatched), HttpStatusCode.BadRequest, "csrf_invalid");
        }
        var owner = (await Me(h.Client)).User;
        var payload = new { owner.RowVersion, isActive = false };
        await Problem(await h.Client.PatchAsJsonAsync(Staff + owner.Id, payload), HttpStatusCode.BadRequest, "csrf_invalid");
        h.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "bogus");
        await Problem(await h.Client.PostAsJsonAsync(Auth + "/logout", new { }), HttpStatusCode.BadRequest, "csrf_invalid");
        h.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(Staff)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Mutate(h.Client, HttpMethod.Post, Auth + "/logout", new { })).StatusCode);
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task T20LoginRateLimitIgnoresUntrustedForwardingHeaders()
    {
        await using var h = await Harness.CreateAsync(fixture);
        for (var i = 0; i < 10; i++)
        {
            h.Client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            h.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100." + (i + 1));
            h.Client.DefaultRequestHeaders.Remove("CF-Connecting-IP");
            h.Client.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113." + (i + 1));
            await Problem(await Login(h.Client, "absent@example.test"), HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        await Problem(await Login(h.Client, "absent@example.test"), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task T20ExplicitTrustedProxySeparatesRealClientIpsButDoesNotTrustCfHeader()
    {
        await using var h = await Harness.CreateAsync(fixture, trustedProxy: true);
        for (var i = 0; i < 11; i++)
        {
            h.Client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            h.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100." + (i + 1));
            await Problem(await Login(h.Client, "absent@example.test"), HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        // Eleven requests for the same forwarded client still reach the 10/minute ceiling.
        for (var i = 0; i < 9; i++) await Login(h.Client, "absent@example.test");
        h.Client.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113.254");
        await Problem(await Login(h.Client, "absent@example.test"), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task T03HttpsHostAndForwardedProtocolAreEnforced()
    {
        await using var h = await Harness.CreateAsync(fixture);
        using var http = h.NewClient();
        http.BaseAddress = new Uri("http://localhost");
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        await Problem(await http.GetAsync(Auth + "/csrf"), HttpStatusCode.BadRequest, "https_required");
        using var wrongHost = h.NewClient();
        wrongHost.BaseAddress = new Uri("https://shop.example.test");
        await Problem(await wrongHost.GetAsync(Auth + "/csrf"), HttpStatusCode.Forbidden, "forbidden");
        await using var trusted = await Harness.CreateAsync(fixture, trustedProxy: true);
        using var forwarded = trusted.NewClient();
        forwarded.BaseAddress = new Uri("http://localhost");
        forwarded.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        forwarded.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.20");
        Assert.Equal(HttpStatusCode.OK, (await forwarded.GetAsync(Auth + "/csrf")).StatusCode);
    }

    [Fact]
    public async Task T14CustomerCookieAndBearerCannotAuthenticateAdmin()
    {
        await using var h = await Harness.CreateAsync(fixture);
        using var customer = h.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/__issue-customer")).StatusCode);
        customer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-an-admin-token");
        await Problem(await customer.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task T22FinalActiveOwnerCannotBeRemovedDisabledOrLockedAndConcurrencyIsChecked()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var owner = (await Me(h.Client)).User;
        await Problem(await Patch(h.Client, owner, new { owner.RowVersion, roles = Array.Empty<string>() }), HttpStatusCode.Conflict, "last_active_owner");
        await Problem(await Patch(h.Client, owner, new { owner.RowVersion, isActive = false }), HttpStatusCode.Conflict, "last_active_owner");
        await Problem(await Patch(h.Client, owner, new { owner.RowVersion, locked = true }), HttpStatusCode.Conflict, "last_active_owner");
        await Problem(await Patch(h.Client, owner, new { rowVersion = "stale", locked = false }), HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(Staff)).StatusCode);
    }

    [Fact]
    public async Task T22ConcurrentOwnerChangesCannotRemoveBothOwners()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var secondOwner = await Create(h.Client, roles: ["owner"]);
        using var second = h.NewClient();
        await FirstLogin(second, secondOwner.Email);
        secondOwner = (await Me(second)).User;
        var firstOwner = (await Me(h.Client)).User;
        var attempts = await Task.WhenAll(
            Patch(h.Client, firstOwner, new { firstOwner.RowVersion, isActive = false }),
            Patch(second, secondOwner, new { secondOwner.RowVersion, isActive = false }));
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var scope = h.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(x => x.IsActive));
    }

    [Fact]
    public async Task T07NormalizedEmailIsUniqueAndFailedResetRollsBackRoleChanges()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        await Problem(await Mutate(h.Client, HttpMethod.Post, Staff, new
        { email = Harness.OwnerEmail.ToUpperInvariant(), displayName = "Duplicate", initialPassword = Password, roles = new[] { "sales" } }), HttpStatusCode.BadRequest, "validation_failed");
        var employee = await Create(h.Client, roles: ["sales"]);
        await Problem(await Patch(h.Client, employee, new { employee.RowVersion, roles = new[] { "owner" }, newPassword = "weak" }), HttpStatusCode.BadRequest, "validation_failed");
        var current = (await (await h.Client.GetAsync(Staff + employee.Id)).Content.ReadFromJsonAsync<StaffView>())!;
        Assert.Equal(new[] { "sales" }, current.Roles);
        Assert.Equal(employee.RowVersion, current.RowVersion);
    }

    [Fact]
    public async Task T23BootstrapRepeatedInvocationCannotOverwriteOrPromoteExistingUser()
    {
        await using var h = await Harness.CreateAsync(fixture);
        Assert.True(await h.BootstrapAsync());
        Assert.False(await h.BootstrapAsync("other@example.test", NewPassword));
        await LoginOk(h.Client);
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Single(await db.UserRoles.ToArrayAsync());
        Assert.DoesNotContain(Password, (await db.Users.SingleAsync()).PasswordHash!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T08T23BootstrapOwnerAlsoMustChangePasswordBeforeAdminAccess()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<BootstrapOwner, bool>>()
                .Handle(new(Harness.OwnerEmail, Password), CancellationToken.None);
            Assert.True(result.IsSuccess && result.Value);
        }
        await LoginOk(h.Client);
        Assert.True((await Me(h.Client)).PasswordChangeRequired);
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Forbidden, "forbidden");
        await Problem(await Mutate(h.Client, HttpMethod.Post, Auth + "/change-password",
            new { currentPassword = Password, newPassword = Password }), HttpStatusCode.BadRequest, "validation_failed");
        await FirstLogin(h.Client, Harness.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(Staff)).StatusCode);
    }

    [Fact]
    public async Task T24SecureResetAndDeactivationRevokeSessionsAndAuditOmitsCredentialsAndPii()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var user = await Create(h.Client, roles: ["sales"]);
        using var employee = h.NewClient();
        await FirstLogin(employee, user.Email);
        user = (await (await h.Client.GetAsync(Staff + user.Id)).Content.ReadFromJsonAsync<StaffView>())!;
        var reset = await Patch(h.Client, user, new { user.RowVersion, newPassword = Password });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.DoesNotContain(Password, await reset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await Problem(await employee.GetAsync(Auth + "/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await LoginOk(employee, user.Email);
        await Problem(await employee.GetAsync("/api/v1/admin/__iam-test/orders.read"), HttpStatusCode.Forbidden, "forbidden");
        user = (await (await h.Client.GetAsync(Staff + user.Id)).Content.ReadFromJsonAsync<StaffView>())!;
        Assert.Equal(HttpStatusCode.OK, (await Patch(h.Client, user, new { user.RowVersion, isActive = false })).StatusCode);
        await Problem(await employee.GetAsync(Auth + "/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditLogs.Where(x => x.EntityId == user.Id).ToArrayAsync();
        Assert.NotEmpty(audit);
        var actorId = (await Me(h.Client)).User.Id;
        Assert.Contains(audit, x => x.ActorType == "staff" && x.ActorId == actorId);
        var evidence = string.Join('|', audit.Select(x => x.Changes)) + h.Logs.Text;
        foreach (var secret in new[] { Password, NewPassword, user.Email, user.DisplayName })
            Assert.DoesNotContain(secret, evidence, StringComparison.Ordinal);
        Assert.Contains("[redacted]", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T26Sliding8HoursNeverExtendsAbsolute7Days()
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        h.Time.Advance(TimeSpan.FromHours(8));
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
        await LoginOk(h.Client);
        for (var i = 0; i < 33; i++)
        {
            h.Time.Advance(TimeSpan.FromHours(5));
            Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(Staff)).StatusCode);
        }
        h.Time.Advance(TimeSpan.FromHours(3));
        await Problem(await h.Client.GetAsync(Staff), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task T27OpenApiContainsIamEndpointsAndContractsWithoutDatabaseSideEffects()
    {
        await using var h = await Harness.CreateAsync(fixture);
        using var response = await h.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/auth/csrf", "/auth/login", "/auth/logout", "/auth/me", "/auth/change-password", "/auth/totp/setup", "/auth/totp/verify", "/staff", "/staff/{id}" })
            Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/admin" + path, out _), path);
        Assert.True(json.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("StaffView", out _));
    }

    [Theory]
    [InlineData("/logout")]
    [InlineData("/change-password")]
    [InlineData("/totp/verify")]
    public async Task T27AuthNoContentRuntimeMatchesGeneratedContract(string endpoint)
    {
        await using var h = await Harness.CreateAsync(fixture);
        await h.BootstrapAsync();
        await LoginOk(h.Client);
        var path = Auth + endpoint;
        using var contract = await h.Client.GetAsync("/openapi/v1.json");
        contract.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await contract.Content.ReadAsStringAsync());

        using var anonymous = h.NewClient();
        using var unauthorized = await anonymous.PostAsJsonAsync(path, new { });
        await Problem(unauthorized, HttpStatusCode.Unauthorized, "unauthenticated");
        using var missingCsrf = await h.Client.PostAsJsonAsync(path, new { });
        await Problem(missingCsrf, HttpStatusCode.BadRequest, "csrf_invalid");

        object body = new { };
        if (endpoint == "/change-password") body = new { currentPassword = Password, newPassword = NewPassword };
        if (endpoint == "/totp/verify")
        {
            using var setupResponse = await Mutate(h.Client, HttpMethod.Post, Auth + "/totp/setup", new { });
            setupResponse.EnsureSuccessStatusCode();
            var setup = (await setupResponse.Content.ReadFromJsonAsync<TotpSetup>())!;
            body = new { code = Code(setup.SharedKey, h.Time.GetUtcNow()) };
        }

        using var success = await Mutate(h.Client, HttpMethod.Post, path, body);
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        Assert.Empty(await success.Content.ReadAsByteArrayAsync());
        var responses = json.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post").GetProperty("responses");
        var status = ((int)success.StatusCode).ToString(CultureInfo.InvariantCulture);
        Assert.True(responses.TryGetProperty(status, out var noContent), $"{path} contract must declare runtime status {status}.");
        Assert.False(noContent.TryGetProperty("content", out _));
        Assert.False(responses.TryGetProperty("200", out _));
        foreach (var errorStatus in new[] { "400", "401", "403" })
            Assert.True(responses.GetProperty(errorStatus).GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    private static async Task FirstLogin(HttpClient client, string email)
    {
        await LoginOk(client, email);
        Assert.Equal(HttpStatusCode.NoContent, (await Mutate(client, HttpMethod.Post, Auth + "/change-password", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        await LoginOk(client, email, NewPassword);
    }

    private static async Task<StaffView> Create(HttpClient client, string[] roles)
    {
        using var response = await Mutate(client, HttpMethod.Post, Staff, new
        { email = Guid.NewGuid().ToString("N") + "@example.test", displayName = "Private Employee", initialPassword = Password, roles });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StaffView>())!;
    }
    private static Task<HttpResponseMessage> Patch(HttpClient client, StaffView user, object body) => Mutate(client, HttpMethod.Patch, Staff + user.Id, body);
    private static Task<HttpResponseMessage> Login(HttpClient client, string email = Harness.OwnerEmail, string password = Password, string? code = null)
        => Mutate(client, HttpMethod.Post, Auth + "/login", new { email, password, totpCode = code });
    private static async Task LoginOk(HttpClient client, string email = Harness.OwnerEmail, string password = Password, string? code = null)
    {
        using var response = await Login(client, email, password, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static async Task<T> Json<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;
    private static async Task<TeaPremium.Api.Iam.AuthResponse> Me(HttpClient client) => await Json<TeaPremium.Api.Iam.AuthResponse>(await client.GetAsync(Auth + "/me"));

    private static async Task<HttpResponseMessage> Mutate(HttpClient client, HttpMethod method, string path, object body)
    {
        using var csrf = await client.GetAsync(Auth + "/csrf");
        csrf.EnsureSuccessStatusCode();
        var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Independent RFC 6238 test client, Identity compatible authenticator algorithm.")]
    private static string Code(string base32, DateTimeOffset now)
    {
        var bits = string.Concat(base32.Select(c => Convert.ToString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        return ((BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(hash[^1] & 15, 4)) & int.MaxValue) % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private sealed class ManualTime : TimeProvider, IClock
    {
        private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string OwnerEmail = "owner@example.test";
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required ManualTime Time { get; init; }
        public required SafeLogs Logs { get; init; }
        public required string CertificatePath { get; init; }
        public HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        public static async Task<Harness> CreateAsync(PlatformDatabaseFixture fixture, bool production = false, bool trustedProxy = false)
        {
            var database = "iam_" + Guid.NewGuid().ToString("N");
            await fixture.ExecuteAsync("CREATE DATABASE " + database);
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options)) await db.Database.MigrateAsync();
            var time = new ManualTime();
            var logs = new SafeLogs();
            var certificatePath = Path.Combine(Path.GetTempPath(), database + ".pfx");
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=IAM test only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
                await File.WriteAllBytesAsync(certificatePath, cert.Export(X509ContentType.Pfx));
            }
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(production ? "Production" : "Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var values = new Dictionary<string, string?>
                    { ["ConnectionStrings:Default"] = connection, ["Iam:KeyCertificatePath"] = certificatePath, ["Iam:AdminHost"] = "localhost" };
                    if (trustedProxy) values["Iam:TrustedProxies:0"] = "10.0.0.1";
                    config.AddInMemoryCollection(values);
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<TimeProvider>(time);
                    services.AddSingleton<IClock>(time);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.AddAuthentication().AddCookie("CustomerCookie");
                    if (trustedProxy) services.AddSingleton<IStartupFilter, TrustedProxyTransport>();
                    services.AddSingleton<IStartupFilter, PermissionProbes>();
                });
            });
            return new Harness { Factory = factory, Client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false }), Time = time, Logs = logs, CertificatePath = certificatePath };
        }

        public async Task<bool> BootstrapAsync(string email = OwnerEmail, string password = Password)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var initial = password + "-Initial!";
            var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<BootstrapOwner, bool>>().Handle(new(email, initial), CancellationToken.None);
            Assert.True(result.IsSuccess);
            if (result.Value)
            {
                var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Email == email);
                var changed = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
                    .Handle(new(user.Id, initial, password), CancellationToken.None);
                Assert.True(changed.IsSuccess && changed.Value);
            }
            return result.Value;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            File.Delete(CertificatePath);
        }
    }

    private sealed class PermissionProbes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/__issue-customer", branch => branch.Run(async context =>
                await context.SignInAsync("CustomerCookie", new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "staff.manage")], "CustomerCookie")))));
            app.Map("/api/v1/admin/__iam-test", branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(endpoints =>
                {
                    foreach (var permission in new[] { "staff.manage", "costs.read", "customers.read", "catalog.write", "inventory.write", "orders.read" })
                        endpoints.MapGet("/" + permission, () => Microsoft.AspNetCore.Http.Results.NoContent()).RequireAuthorization(permission);
                });
            });
            next(app);
        };
    }

    private sealed class TrustedProxyTransport : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class SafeLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public string Text => string.Join('\n', _lines);
        public ILogger CreateLogger(string categoryName) => new Capture(_lines);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }
}
