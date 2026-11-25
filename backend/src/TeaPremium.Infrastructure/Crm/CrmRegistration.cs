using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Infrastructure.Orders;

namespace TeaPremium.Infrastructure.Crm;

public static class CrmRegistration
{
    public static IServiceCollection AddCrm(this IServiceCollection services)
    {
        services.AddScoped<CustomerIdentityAudit>();
        services.AddScoped<ICustomerQueries, EfCustomerQueries>();
        services.AddScoped<ICustomerStore, EfCustomerStore>();
        services.AddScoped<ICustomerOrderQueries, EfCustomerOrderQueries>();
        return services;
    }
}
