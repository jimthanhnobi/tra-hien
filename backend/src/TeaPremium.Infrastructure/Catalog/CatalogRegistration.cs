using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;

namespace TeaPremium.Infrastructure.Catalog;

public static class CatalogRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddScoped<CatalogWorkflow>();
        services.AddScoped<ICatalogStore, EfCatalogStore>();
        services.AddScoped<ICatalogContent, CatalogContentSanitizer>();
        return services;
    }
}
