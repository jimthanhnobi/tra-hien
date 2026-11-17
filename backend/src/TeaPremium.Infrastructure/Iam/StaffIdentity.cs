using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Iam;

public sealed class StaffIdentity(AppDbContext db, UserManager<StaffUser> users,
    IUserStore<StaffUser> store, IClock clock, IDataProtectionProvider protection, IAuditContext auditContext) : IStaffIdentity
{
    private readonly IDataProtector _pending = protection.CreateProtector("TeaPremium.Staff.PendingAuthenticator.v1");
    private const string PendingKey = "totp.pending_key";
    private const string PendingAt = "totp.pending_at";
    private const string FailedInstants = "auth.failed_instants";
    private static readonly LoginOutcome Rejected = new(null, null, false);

    public async Task<Result<LoginOutcome>> LoginAsync(LoginStaff command, CancellationToken ct)
    {
        var normalized = users.NormalizeEmail(command.Email);
        var id = await db.Users.Where(x => x.NormalizedEmail == normalized).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (id is null)
        {
            // Equalize the expensive hash work without storing or logging a credential.
            var dummy = new StaffUser();
            users.PasswordHasher.VerifyHashedPassword(dummy, DummyPasswordHash.Value, command.Password);
            return Result.Success(Rejected);
        }
        var user = await LockAsync(id.Value, ct);
        if (user is null || !Usable(user)) return Result.Success(Rejected);
        if (!await users.CheckPasswordAsync(user, command.Password))
        {
            await FailedAsync(user);
            return Result.Success(Rejected);
        }
        if (user.TwoFactorEnabled && !await users.VerifyTwoFactorTokenAsync(user, "StaffTotp", command.TotpCode ?? ""))
        {
            await FailedAsync(user);
            return Result.Success(Rejected);
        }
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        Ensure(await users.RemoveAuthenticationTokenAsync(user, StaffTotp.Provider, FailedInstants));
        Ensure(await users.UpdateAsync(user));
        return Result.Success(new LoginOutcome(await ViewAsync(user, ct), user.SecurityStamp, user.TwoFactorEnabled));
    }

    public async Task<Result<bool>> ChangePasswordAsync(ChangeStaffPassword command, CancellationToken ct)
    {
        var user = await LockAsync(command.Id, ct);
        if (user is null || !Usable(user)) return Forbidden<bool>();
        if (!await users.CheckPasswordAsync(user, command.CurrentPassword))
        {
            await FailedAsync(user);
            return Result.Success(false);
        }
        var result = await users.ChangePasswordAsync(user, command.CurrentPassword, command.NewPassword);
        if (!result.Succeeded) return Invalid<bool>();
        user.MustChangePassword = false;
        Ensure(await users.UpdateAsync(user));
        return Result.Success(true);
    }

    public async Task<Result<TotpSetup>> SetupTotpAsync(SetupStaffTotp command, CancellationToken ct)
    {
        var user = await LockAsync(command.Id, ct);
        if (user is null || !Usable(user) || user.MustChangePassword) return Forbidden<TotpSetup>();
        // Existing verified secrets are never replaced by an enrollment request.
        if (user.TwoFactorEnabled) return Conflict<TotpSetup>("totp_already_enabled");
        var key = users.GenerateNewAuthenticatorKey();
        Ensure(await users.SetAuthenticationTokenAsync(user, StaffTotp.Provider, PendingKey, _pending.Protect(key)));
        Ensure(await users.SetAuthenticationTokenAsync(user, StaffTotp.Provider, PendingAt,
            clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        var label = Uri.EscapeDataString("TeaPremium:" + user.Email);
        return Result.Success(new TotpSetup(key, $"otpauth://totp/{label}?secret={key}&issuer=TeaPremium&digits=6&period=30"));
    }

    public async Task<Result<bool>> VerifyTotpAsync(VerifyStaffTotp command, CancellationToken ct)
    {
        var user = await LockAsync(command.Id, ct);
        if (user is null || !Usable(user) || user.MustChangePassword || user.TwoFactorEnabled) return Forbidden<bool>();
        var protectedKey = await users.GetAuthenticationTokenAsync(user, StaffTotp.Provider, PendingKey);
        var at = await users.GetAuthenticationTokenAsync(user, StaffTotp.Provider, PendingAt);
        var now = clock.UtcNow;
        var fresh = long.TryParse(at, CultureInfo.InvariantCulture, out var seconds)
            && now.ToUnixTimeSeconds() >= seconds && now.ToUnixTimeSeconds() - seconds < 600;
        var key = protectedKey is null ? null : _pending.Unprotect(protectedKey);
        var step = fresh ? StaffTotp.Match(key, command.Code, now, null) : null;
        if (step is null)
        {
            await FailedAsync(user);
            return Result.Success(false);
        }
        await ((IUserAuthenticatorKeyStore<StaffUser>)store).SetAuthenticatorKeyAsync(user, key!, ct);
        Ensure(await users.SetAuthenticationTokenAsync(user, StaffTotp.Provider, StaffTotp.LastStep,
            step.Value.ToString(CultureInfo.InvariantCulture)));
        Ensure(await users.RemoveAuthenticationTokenAsync(user, StaffTotp.Provider, PendingKey));
        Ensure(await users.RemoveAuthenticationTokenAsync(user, StaffTotp.Provider, PendingAt));
        Ensure(await users.SetTwoFactorEnabledAsync(user, true));
        Ensure(await users.UpdateSecurityStampAsync(user));
        user.AccessFailedCount = 0;
        Ensure(await users.UpdateAsync(user));
        return Result.Success(true);
    }

    public async Task<Result<StaffView>> CreateAsync(CreateStaff command, CancellationToken ct)
    {
        await AdministrationLockAsync(ct);
        if (!await CanManageAsync(command.ActorId, command.ActorStamp, command.Roles.Contains("owner", StringComparer.Ordinal), ct)) return Forbidden<StaffView>();
        var user = NewUser(command.Email, command.DisplayName);
        var result = await users.CreateAsync(user, command.InitialPassword);
        if (!result.Succeeded) return Invalid<StaffView>();
        Ensure(await users.AddToRolesAsync(user, command.Roles));
        AuditRoles(user.Id, [], command.Roles);
        return Result.Success(await ViewAsync(user, ct));
    }

    public async Task<Result<StaffView>> PatchAsync(PatchStaff command, CancellationToken ct)
    {
        // Serialize owner membership changes, including simultaneous deactivation of two owners.
        await AdministrationLockAsync(ct);
        if (!await CanManageAsync(command.ActorId, command.ActorStamp, command.Roles?.Contains("owner", StringComparer.Ordinal) == true, ct)) return Forbidden<StaffView>();
        var user = await LockAsync(command.Id, ct);
        if (user is null) return Result.Failure<StaffView>(new Error("not_found", "Không tìm thấy nhân viên.", ErrorType.NotFound));
        if (user.ConcurrencyStamp != command.RowVersion) return Conflict<StaffView>("concurrency_conflict");
        var roles = await users.GetRolesAsync(user);
        var removesActiveOwner = roles.Contains("owner", StringComparer.Ordinal) && user.IsActive
            && (command.IsActive == false || command.Locked == true || command.Roles is not null && !command.Roles.Contains("owner", StringComparer.Ordinal));
        if (removesActiveOwner)
        {
            var now = clock.UtcNow;
            var otherOwner = await (from u in db.Users
                                    join r in db.UserRoles on u.Id equals r.UserId
                                    where r.RoleId == IamModel.OwnerRole && u.Id != user.Id && u.IsActive
                                        && (u.LockoutEnd == null || u.LockoutEnd <= now)
                                    select u.Id).AnyAsync(ct);
            if (!otherOwner) return Conflict<StaffView>("last_active_owner");
        }
        if (command.IsActive is { } active) user.IsActive = active;
        if (command.Locked is { } locked) user.LockoutEnd = locked ? DateTimeOffset.MaxValue : null;
        if (command.Roles is not null)
        {
            Ensure(await users.RemoveFromRolesAsync(user, roles.Except(command.Roles, StringComparer.Ordinal)));
            Ensure(await users.AddToRolesAsync(user, command.Roles.Except(roles, StringComparer.Ordinal)));
            AuditRoles(user.Id, roles.ToArray(), command.Roles);
        }
        if (command.NewPassword is not null)
        {
            // Owner supplies a strong temporary password via HTTPS/out-of-band delivery.
            // No reset token or plaintext credential is returned by this API.
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var reset = await users.ResetPasswordAsync(user, token, command.NewPassword);
            if (!reset.Succeeded) return Invalid<StaffView>();
            user.MustChangePassword = true;
        }
        Ensure(await users.UpdateSecurityStampAsync(user));
        Ensure(await users.UpdateAsync(user));
        return Result.Success(await ViewAsync(user, ct));
    }

    public async Task<Result<bool>> BootstrapAsync(BootstrapOwner command, CancellationToken ct)
    {
        await AdministrationLockAsync(ct);
        // No subsequent invocation can modify credentials, reactivate, or promote anyone.
        if (await db.UserRoles.AnyAsync(x => x.RoleId == IamModel.OwnerRole, ct)) return Result.Success(false);
        if (await users.FindByEmailAsync(command.Email) is not null) return Conflict<bool>("bootstrap_account_exists");
        var user = NewUser(command.Email, "Owner");
        if (!(await users.CreateAsync(user, command.Password)).Succeeded) return Invalid<bool>();
        Ensure(await users.AddToRoleAsync(user, "owner"));
        return Result.Success(true);
    }

    public async Task<StaffSession?> SessionAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return user is null || !Usable(user) ? null : new StaffSession(await ViewAsync(user, ct), user.SecurityStamp!);
    }

    public async Task<StaffPage> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var count = await db.Users.CountAsync(ct);
        var rows = await db.Users.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        var views = new List<StaffView>();
        foreach (var row in rows) views.Add(await ViewAsync(row, ct));
        return new StaffPage(views.ToArray(), page, pageSize, count);
    }

    public async Task<StaffView?> FindAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return user is null ? null : await ViewAsync(user, ct);
    }

    private StaffUser NewUser(string email, string name) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = email,
        Email = email,
        DisplayName = name,
        CreatedAt = clock.UtcNow.UtcDateTime,
        IsActive = true,
        MustChangePassword = true,
        LockoutEnabled = true
    };

    private async Task<StaffUser?> LockAsync(Guid id, CancellationToken ct)
    {
        // Framework UserManager operations join the command's AppDbContext/UoW transaction.
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM iam.staff_users WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is not null) await db.Entry(user).ReloadAsync(ct);
        return user;
    }

    private Task<int> AdministrationLockAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(830081)", ct);

    private async Task<bool> CanManageAsync(Guid actorId, string actorStamp, bool assignsOwner, CancellationToken ct)
    {
        var actor = await LockAsync(actorId, ct);
        if (actor is null || !Usable(actor) || actor.MustChangePassword || actor.SecurityStamp != actorStamp) return false;
        var view = await ViewAsync(actor, ct);
        return view.Permissions.Contains("staff.manage", StringComparer.Ordinal)
            && (!assignsOwner || view.Roles.Contains("owner", StringComparer.Ordinal));
    }

    private void AuditRoles(Guid id, string[] before, string[] after)
    {
        // Membership has a composite key. Record the approved audit envelope against the
        // staff UUID, in the same UoW, rather than inventing a composite-key encoding.
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.UtcNow.UtcDateTime,
            ActorType = auditContext.ActorType,
            ActorId = auditContext.ActorId,
            EntityType = "iam.staff_users",
            EntityId = id,
            Action = "state_change",
            Changes = JsonSerializer.Serialize(new { roles = new { before, after } }),
            CorrelationId = auditContext.CorrelationId
        });
    }

    private bool Usable(StaffUser user) => user.IsActive && (user.LockoutEnd is null || user.LockoutEnd <= clock.UtcNow);

    private async Task FailedAsync(StaffUser user)
    {
        // FR-IAM-01: five failures in a rolling 15-minute window, not five failures
        // accumulated across an arbitrarily long period. The locked row serializes it.
        var now = clock.UtcNow;
        var previous = await users.GetAuthenticationTokenAsync(user, StaffTotp.Provider, FailedInstants);
        var instants = (previous ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.Parse(x, CultureInfo.InvariantCulture))
            .Where(x => x > now.AddMinutes(-15).ToUnixTimeSeconds() && x <= now.ToUnixTimeSeconds())
            .Append(now.ToUnixTimeSeconds()).ToArray();
        user.AccessFailedCount = instants.Length;
        if (user.AccessFailedCount >= 5)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = now.AddMinutes(15);
            // Previously issued sessions stay revoked even after the lock expires.
            Ensure(await users.UpdateSecurityStampAsync(user));
            Ensure(await users.RemoveAuthenticationTokenAsync(user, StaffTotp.Provider, FailedInstants));
        }
        else Ensure(await users.SetAuthenticationTokenAsync(user, StaffTotp.Provider, FailedInstants, string.Join(',', instants)));
        Ensure(await users.UpdateAsync(user));
    }

    private async Task<StaffView> ViewAsync(StaffUser user, CancellationToken ct)
    {
        var roleIds = db.UserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId);
        var roles = await db.Roles.Where(x => roleIds.Contains(x.Id)).Select(x => x.Name!).OrderBy(x => x).ToArrayAsync(ct);
        var permissions = await db.RoleClaims.Where(x => roleIds.Contains(x.RoleId) && x.ClaimType == "permission")
            .Select(x => x.ClaimValue!).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        return new StaffView(user.Id, user.Email!, user.DisplayName, user.IsActive, user.MustChangePassword,
            user.TwoFactorEnabled, user.ConcurrencyStamp!, roles, permissions);
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity persistence operation failed.");
    }
    private static Result<T> Invalid<T>() => Result.Failure<T>(new Error("validation_failed", "Thông tin hoặc mật khẩu không hợp lệ.", ErrorType.Validation));
    private static Result<T> Forbidden<T>() => Result.Failure<T>(new Error("forbidden", "Không được phép thực hiện thao tác.", ErrorType.Forbidden));
    private static Result<T> Conflict<T>(string code) => Result.Failure<T>(new Error(code, "Thao tác xung đột với trạng thái hiện tại.", ErrorType.Conflict));

    private static readonly Lazy<string> DummyPasswordHash = new(() => new PasswordHasher<StaffUser>().HashPassword(new StaffUser(), Guid.NewGuid().ToString()));
}
