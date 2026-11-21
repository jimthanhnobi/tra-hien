using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Pricing;
using TeaPremium.Infrastructure.Pricing;

namespace TeaPremium.Infrastructure.Catalog;

public static class CatalogRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddScoped<CatalogWorkflow>();
        services.AddScoped<ProductWorkflow>();
        services.AddScoped<IProductImportReference, EfProductImportReference>();
        services.AddScoped<IProductStore, EfProductStore>();
        services.AddScoped<ISalePriceStore, EfSalePriceStore>();
        services.AddScoped<ProductAudit>();
        services.AddScoped<TeaPremium.Infrastructure.Inventory.EfCatalogInventoryRead>();
        services.AddScoped<TeaPremium.Application.Inventory.ICatalogInventoryRead>(provider => provider.GetRequiredService<TeaPremium.Infrastructure.Inventory.EfCatalogInventoryRead>());
        services.AddScoped<IVariantUsage>(provider => provider.GetRequiredService<TeaPremium.Infrastructure.Inventory.EfCatalogInventoryRead>());
        services.AddHttpClient<TeaPremium.Infrastructure.Outbox.StorefrontInvalidateHandler>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<TeaPremium.Application.Outbox.IOutboxHandler>(provider => provider.GetRequiredService<TeaPremium.Infrastructure.Outbox.StorefrontInvalidateHandler>());
        services.AddScoped<ICatalogStore, EfCatalogStore>();
        services.AddScoped<ICatalogContent, CatalogContentSanitizer>();
        return services;
    }
}
