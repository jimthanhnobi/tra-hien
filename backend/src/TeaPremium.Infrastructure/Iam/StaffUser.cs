using Microsoft.AspNetCore.Identity;
using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Iam;

[Audited]
public sealed class StaffUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
