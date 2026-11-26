using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeaPremium.Application.Payments;

namespace TeaPremium.Infrastructure.Payments;

public static class PaymentsRegistration
{
    public static IServiceCollection AddPayments(this IServiceCollection services)
    {
        services.TryAddScoped<IOrderPaymentLedgerReader, EfOrderPaymentLedgerReader>();
        services.AddScoped<IPaymentRepository, EfPaymentRepository>();
        services.AddScoped<IRefundRepository, EfRefundRepository>();
        services.AddScoped<IReturnRefundPort, EfReturnRefundStore>();
        services.AddScoped<IPaymentQueries, EfPaymentQueries>();
        return services;
    }
}
