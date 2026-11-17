using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Iam;

public sealed class ProtectedStaffStore(AppDbContext db, IDataProtectionProvider protection)
    : UserStore<StaffUser, IdentityRole<Guid>, AppDbContext, Guid>(db)
{
    private readonly IDataProtector _protector = protection.CreateProtector("TeaPremium.Staff.Authenticator.v1");

    public override Task SetAuthenticatorKeyAsync(StaffUser user, string key, CancellationToken cancellationToken)
        => base.SetAuthenticatorKeyAsync(user, _protector.Protect(key), cancellationToken);

    public override async Task<string?> GetAuthenticatorKeyAsync(StaffUser user, CancellationToken cancellationToken)
    {
        var protectedKey = await base.GetAuthenticatorKeyAsync(user, cancellationToken);
        return protectedKey is null ? null : _protector.Unprotect(protectedKey);
    }
}
