using TeaPremium.Infrastructure.Outbox;

namespace TeaPremium.Api.Outbox;

public sealed class OutboxDispatcherOptions
{
    public int BatchSize { get; set; } = 20;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> DispatchFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "OutboxDispatchFailed"), "Outbox dispatch pass failed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default"))) return;
        var settings = options.Value;
        if (settings.BatchSize is < 1 or > 100 || settings.PollInterval < TimeSpan.FromMilliseconds(100))
            throw new InvalidOperationException("Invalid outbox dispatcher settings.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
                await processor.ProcessAsync(settings.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Exceptions can contain SQL parameters or provider payloads. Keep logs fixed.
                DispatchFailed(logger, null);
            }

            await Task.Delay(settings.PollInterval, stoppingToken);
        }
    }
}
