using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Iam;

namespace TeaPremium.Infrastructure.Iam;

internal static class IamModel
{
    internal static readonly Guid OwnerRole = new("00000000-0000-7000-8000-000000000001");
    internal static readonly Guid SalesRole = new("00000000-0000-7000-8000-000000000002");
    internal static readonly Guid ContentRole = new("00000000-0000-7000-8000-000000000003");

    internal static void Configure(ModelBuilder model)
    {
        // P0-08 specifies seven Identity tables; passkeys are outside the approved scope.
        model.Ignore<IdentityUserPasskey<Guid>>();
        model.Ignore<IdentityPasskeyData>();
        model.Entity<StaffUser>(b =>
        {
            b.ToTable("staff_users", "iam");
            b.HasKey(x => x.Id).HasName("pk_staff_users");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).IsRequired();
            b.Property(x => x.NormalizedEmail).IsRequired();
            b.Property(x => x.DisplayName).IsRequired();
            b.HasIndex(x => x.NormalizedEmail).IsUnique().HasDatabaseName("ux_staff_users_normalized_email");
            b.HasIndex(x => x.NormalizedUserName).HasDatabaseName("ux_staff_users_normalized_user_name");
        });
        model.Entity<IdentityRole<Guid>>(b =>
        {
            b.ToTable("roles", "iam");
            b.HasKey(x => x.Id).HasName("pk_roles");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.NormalizedName).HasDatabaseName("ux_roles_normalized_name");
            foreach (var (name, id) in new[] { ("owner", OwnerRole), ("sales", SalesRole), ("content", ContentRole) })
                b.HasData(new IdentityRole<Guid> { Id = id, Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = id.ToString() });
        });
        model.Entity<IdentityUserRole<Guid>>(b =>
        {
            b.ToTable("user_roles", "iam");
            b.HasKey(x => new { x.UserId, x.RoleId }).HasName("pk_user_roles");
            b.HasIndex(x => x.RoleId).HasDatabaseName("ix_user_roles_role_id");
        });
        model.Entity<IdentityRoleClaim<Guid>>(b =>
        {
            b.ToTable("role_claims", "iam");
            b.HasKey(x => x.Id).HasName("pk_role_claims");
            b.HasIndex(x => x.RoleId).HasDatabaseName("ix_role_claims_role_id");
            var claimId = 1;
            foreach (var (name, id) in new[] { ("owner", OwnerRole), ("sales", SalesRole), ("content", ContentRole) })
                foreach (var permission in StaffPermissions.ByRole[name])
                    b.HasData(new IdentityRoleClaim<Guid> { Id = claimId++, RoleId = id, ClaimType = "permission", ClaimValue = permission });
        });
        model.Entity<IdentityUserClaim<Guid>>(b =>
        {
            b.ToTable("user_claims", "iam");
            b.HasKey(x => x.Id).HasName("pk_user_claims");
            b.HasIndex(x => x.UserId).HasDatabaseName("ix_user_claims_user_id");
        });
        model.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.ToTable("user_logins", "iam");
            b.HasKey(x => new { x.LoginProvider, x.ProviderKey }).HasName("pk_user_logins");
            b.HasIndex(x => x.UserId).HasDatabaseName("ix_user_logins_user_id");
        });
        model.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.ToTable("user_tokens", "iam");
            b.HasKey(x => new { x.UserId, x.LoginProvider, x.Name }).HasName("pk_user_tokens");
        });
        foreach (var entity in model.Model.GetEntityTypes().Where(x => x.GetSchema() == "iam"))
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"fk_{entity.GetTableName()}_{fk.PrincipalEntityType.GetTableName()}_{(fk.Properties[0].Name == "RoleId" ? "role_id" : "user_id")}");
    }
}
