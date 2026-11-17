using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeaPremium.Api.Outbox;

namespace TeaPremium.IntegrationTests;

public sealed class OutboxLoggingTests
{
    [Fact]
    public async Task DispatcherDoesNotLogProviderExceptionOrSecret()
    {
        const string secret = "customer@example.test token-PRIVATE";
        var logger = new CaptureLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "configured" }).Build();
        using var dispatcher = new OutboxDispatcher(new ThrowingScopeFactory(secret), configuration,
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(100) }), logger);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Outbox dispatch pass failed.", entry.Message);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingScopeFactory(string secret) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException(secret);
    }

    private sealed class CaptureLogger : ILogger<OutboxDispatcher>
    {
        public TaskCompletionSource<(string Message, Exception? Exception)> Entry { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new EmptyScope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entry.TrySetResult((formatter(state, exception), exception));
        }

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
