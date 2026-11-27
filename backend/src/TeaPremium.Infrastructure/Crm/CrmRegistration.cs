using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Crm.Contracts;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public static class CrmRegistration
{
    public static IServiceCollection AddCrm(this IServiceCollection services)
    {
        services.AddScoped<CustomerIdentityAudit>();
        services.AddScoped<ICustomerQueries, EfCustomerQueries>();
        services.AddScoped<ICustomerStore, EfCustomerStore>();
        services.AddScoped<ICustomerOrderResolver, EfCustomerOrderResolver>();
        services.AddScoped<IInteractionQueries, EfInteractionQueries>();
        services.AddScoped<IInteractionStore, EfInteractionStore>();
        services.AddScoped<ICustomerImportStore>(sp => new EfCustomerImportStore(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetService<ICustomerImportSaveHook>()));
        return services;
    }
}
