using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Pricing;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Infrastructure.Inventory;
using TeaPremium.Infrastructure.Payments;
using TeaPremium.Infrastructure.Persistence;
using TeaPremium.Infrastructure.Pricing;

namespace TeaPremium.Infrastructure.Orders;

public static class OrdersRegistration
{
    public static IServiceCollection AddOrders(this IServiceCollection services)
    {
        services.AddScoped<IProductSoldOutSync, EfProductSoldOutSync>();
        services.AddScoped<IInventoryReservationService, EfInventoryReservationService>();
        services.AddScoped<IVoucherRedemptionPort, EfVoucherRedemptionStore>();
        services.AddScoped<IOrderPricingRead, EfOrderPricingRead>();
        services.AddSingleton<IOrderCodeGenerator, RandomOrderCodeGenerator>();
        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<ICustomerOrderQueries, EfCustomerOrderQueries>();
        services.TryAddScoped<IOrderPaymentLedgerReader, EfOrderPaymentLedgerReader>();
        services.AddScoped<IVariantUsage, OrderVariantUsage>();

        // G22: register exactly one CompositeOrderOutboxHandler per Order* message type.
        foreach (var messageType in OrderEventTypes.All)
        {
            var captured = messageType;
            services.AddScoped<IOutboxHandler>(provider => new CompositeOrderOutboxHandler(
                captured,
                provider.GetRequiredService<AppDbContext>(),
                provider.GetServices<IOrderOutboxStep>(),
                provider.GetRequiredService<ILogger<CompositeOrderOutboxHandler>>()));
        }

        return services;
    }
}
