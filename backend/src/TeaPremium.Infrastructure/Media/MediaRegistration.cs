using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeaPremium.Application.Media;

namespace TeaPremium.Infrastructure.Media;

public static class MediaRegistration
{
    public static IServiceCollection AddMedia(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton(provider =>
        {
            var options = new MediaStorageOptions();
            // Resolve after host/test configuration is finalized. Never put credentials in appsettings.
            provider.GetRequiredService<IConfiguration>().GetSection("MediaStorage").Bind(options);
            return options;
        });
        var local = environment.IsDevelopment() || environment.IsEnvironment("CI") || environment.IsEnvironment("Test");
        if (local) services.AddSingleton<FakeMediaStorage>();
        services.AddSingleton<IMediaStorage>(provider =>
        {
            var options = provider.GetRequiredService<MediaStorageOptions>();
            if (options.Provider == "Fake" && local) return provider.GetRequiredService<FakeMediaStorage>();
            if (options.Provider != "R2") throw new MediaStorageException();
            options.Validate(environment.IsProduction());
            return new R2MediaStorage(options);
        });
        services.AddSingleton<IMediaContentValidator, FfmpegContentValidator>();
        services.AddScoped<IMediaWorkflow, EfMediaWorkflow>();
        services.AddScoped<UploadCleanup>();
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default")) && configuration["MediaStorage:Provider"] is "R2" or "Fake")
            services.AddHostedService<ExpireUploadSessions>();
        return services;
    }
}
