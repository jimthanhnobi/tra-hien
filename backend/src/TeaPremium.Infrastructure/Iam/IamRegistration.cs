using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Iam;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Iam;

public static class IamRegistration
{
    public static IServiceCollection AddStaffIdentity(this IServiceCollection services, IConfiguration configuration, bool production)
    {
        services.AddIdentityCore<StaffUser>(options =>
        {
            // Pin the seven-table model identically for runtime and design-time contexts.
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireDigit = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;
            options.Tokens.AuthenticatorTokenProvider = "StaffTotp";
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders().AddTokenProvider<StaffTotp>("StaffTotp");
        services.AddScoped<IUserStore<StaffUser>, ProtectedStaffStore>();
        services.AddScoped<IStaffIdentity, StaffIdentity>();
        services.AddScoped<IStaffDirectory, EfStaffDirectory>();
        var protection = services.AddDataProtection().SetApplicationName("teapremium");
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default")))
        {
            if (production && Uri.CheckHostName(configuration["Iam:AdminHost"]) == UriHostNameType.Unknown)
                throw new InvalidOperationException("Iam:AdminHost must identify the production Admin origin host.");
            protection.PersistKeysToDbContext<AppDbContext>();
            var path = configuration["Iam:KeyCertificatePath"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration["Iam:KeyCertificatePassword"]);
                if (!certificate.HasPrivateKey) throw new InvalidOperationException("Data Protection requires a certificate private key.");
                protection.ProtectKeysWithCertificate(certificate);
            }
            else if (production)
                throw new InvalidOperationException("Iam:KeyCertificatePath is required in Production with a database.");
        }
        return services;
    }
}
