using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;

namespace TeaPremium.Infrastructure.Notifications;

public static class NotificationRegistration
{
    public static IServiceCollection AddNotifications(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton(provider =>
        {
            // Resolve after host/test configuration overrides are finalized. Never put secrets in appsettings.
            var options = NotificationOptions.Bind(provider.GetRequiredService<IConfiguration>());
            options.Validate(environment.EnvironmentName);
            return options;
        });

        services.AddSingleton<TelegramRateLimiter>();
        // Direct SocketsHttpHandler avoids IHttpClientFactory's default URI Information logger
        // that would otherwise log /bot<TELEGRAM_BOT_TOKEN>/sendMessage.
        services.AddSingleton(provider => TelegramHttpClient.CreateDefault(provider.GetRequiredService<NotificationOptions>()));
        services.AddSingleton<ISmtpTransport, BclSmtpTransport>();

        services.AddSingleton<INotificationChannel, TelegramNotificationChannel>();
        services.AddSingleton<INotificationChannel, SmtpNotificationChannel>();

        services.AddScoped<IDailyDigestRead, EfDailyDigestRead>();
        services.AddSingleton<TechnicalAlertNotifier>();
        services.AddSingleton<IOutboxDeadNotifier>(provider => provider.GetRequiredService<TechnicalAlertNotifier>());
        services.AddSingleton<IInventoryMismatchNotifier>(provider => provider.GetRequiredService<TechnicalAlertNotifier>());

        services.AddScoped<IOutboxHandler, NotificationDeliveryHandler>();
        // G22: plug into P2-05's CompositeOrderOutboxHandler rather than registering a duplicate IOutboxHandler for OrderPlaced.
        services.AddScoped<IOrderOutboxStep, OrderPlacedNotificationHandler>();

        return services;
    }
}
