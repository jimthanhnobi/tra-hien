using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Outbox;

namespace TeaPremium.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:Default must be configured before database access.");
            options.UseNpgsql(connectionString);
            options.AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>());
        });
        services.AddScoped<IAuditContext, SystemAuditContext>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();
        services.AddScoped<OutboxBatchProcessor>();
        return services;
    }
}
