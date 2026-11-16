using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:Default must be configured before database access.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        return services;
    }
}
