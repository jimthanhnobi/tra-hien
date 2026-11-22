using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TeaPremium.Application.Media;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

internal sealed class TestAppScope
{
    private static int _ipSequence;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string RuntimeConnection { get; }
    public IPAddress SyntheticIp { get; }
    public FakeMediaStorage FakeMedia { get; } = new();
    public ConcurrentDictionary<string, string?> Values { get; }
    public IConfigurationRoot Configuration { get; }

    public TestAppScope(string runtimeConnection, IDictionary<string, string?> initialConfig)
    {
        RuntimeConnection = runtimeConnection;
        var seq = Interlocked.Increment(ref _ipSequence) - 1;
        var last = (seq % 254) + 1;
        var high = seq / 254;
        SyntheticIp = new IPAddress([10, (byte)((high >> 8) & 0xFF), (byte)(high & 0xFF), (byte)last]);
        Values = new ConcurrentDictionary<string, string?>(initialConfig, StringComparer.OrdinalIgnoreCase);
        Configuration = new ConfigurationBuilder()
            .Add(new DictionaryConfigurationSource(Values))
            .Build();
    }
}

public sealed class TestAppHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program>? _ownedFactory;
    private readonly SharedTestAppHost? _sharedHost;
    private readonly TestAppScope? _scope;
    private readonly IServiceProvider _services;

    internal TestAppHost(WebApplicationFactory<Program> ownedFactory)
    {
        _ownedFactory = ownedFactory;
        _services = ownedFactory.Services;
    }

    internal TestAppHost(SharedTestAppHost sharedHost, TestAppScope scope)
    {
        _sharedHost = sharedHost;
        _scope = scope;
        _services = new ScopedTestServiceProvider(sharedHost.Factory.Services, scope);
    }

    public IServiceProvider Services => _services;

    public HttpClient CreateClient(WebApplicationFactoryClientOptions? options = null)
    {
        if (_ownedFactory is not null)
            return options is null ? _ownedFactory.CreateClient() : _ownedFactory.CreateClient(options);

        var client = options is null
            ? _sharedHost!.Factory.CreateClient()
            : _sharedHost!.Factory.CreateClient(options);
        client.DefaultRequestHeaders.Add(SharedTestAppHost.ScopeHeader, _scope!.Id);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedFactory is not null)
            await _ownedFactory.DisposeAsync();
        else if (_sharedHost is not null && _scope is not null)
            _sharedHost.Unregister(_scope.Id);
    }
}

internal sealed class SharedTestAppHost
{
    internal const string ScopeHeader = "X-TeaPremium-Test-Scope";
    internal static readonly AsyncLocal<TestAppScope?> CurrentScope = new();

    private readonly ConcurrentDictionary<string, TestAppScope> _scopes = new(StringComparer.Ordinal);

    public WebApplicationFactory<Program> Factory { get; }

    public SharedTestAppHost(SharedPostgresServer server, string keyringRuntimeConnection)
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = keyringRuntimeConnection,
                    ["Iam:KeyCertificatePath"] = server.CertificatePath
                });
                configuration.Add(new AsyncLocalScopeConfigurationSource());
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);
                services.AddSingleton<IStartupFilter>(new ScopeMiddlewareFilter(_scopes));
                services.AddDbContext<AppDbContext>(options =>
                {
                    var connectionString = CurrentScope.Value?.RuntimeConnection ?? keyringRuntimeConnection;
                    options.UseNpgsql(connectionString);
                    options.UseModel(SharedPostgresServer.SharedModel);
                });
                services.RemoveAll<MediaStorageOptions>();
                services.RemoveAll<IMediaStorage>();
                services.RemoveAll<IMediaContentValidator>();
                services.AddScoped(provider =>
                {
                    var options = new MediaStorageOptions();
                    provider.GetRequiredService<IConfiguration>().GetSection("MediaStorage").Bind(options);
                    return options;
                });
                services.AddScoped<IMediaStorage>(provider =>
                {
                    var options = provider.GetRequiredService<MediaStorageOptions>();
                    if (options.Provider == "Fake")
                        return CurrentScope.Value?.FakeMedia ?? provider.GetRequiredService<FakeMediaStorage>();
                    if (options.Provider != "R2") throw new MediaStorageException();
                    options.Validate(false);
                    return new R2MediaStorage(options);
                });
                services.AddScoped<IMediaContentValidator, FfmpegContentValidator>();
                services.AddDataProtection().SetApplicationName("teapremium").PersistKeysToDbContext<AppDbContext>()
                    .ProtectKeysWithCertificate(server.Certificate);
            });
        });

        // Warm up host & Data Protection keyring once.
        _ = Factory.Server;
    }

    public TestAppHost Register(TestAppScope scope)
    {
        _scopes[scope.Id] = scope;
        return new TestAppHost(this, scope);
    }

    public void Unregister(string id) => _scopes.TryRemove(id, out _);

    private sealed class ScopeMiddlewareFilter(ConcurrentDictionary<string, TestAppScope> scopes) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers.TryGetValue(ScopeHeader, out var values))
                {
                    var scopeId = values.ToString();
                    if (!scopes.TryGetValue(scopeId, out var scope))
                        throw new InvalidOperationException($"Unknown or disposed test scope '{scopeId}'.");
                    context.Request.Headers.Remove(ScopeHeader);
                    var previous = CurrentScope.Value;
                    CurrentScope.Value = scope;
                    context.Connection.RemoteIpAddress ??= scope.SyntheticIp;
                    try { await continuation(); }
                    finally { CurrentScope.Value = previous; }
                    return;
                }
                await continuation();
            });
            next(app);
        };
    }
}

internal sealed class ScopedTestServiceProvider(IServiceProvider inner, TestAppScope scope) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory))
            return new ScopedTestServiceScopeFactory(inner.GetRequiredService<IServiceScopeFactory>(), scope);

        var previous = SharedTestAppHost.CurrentScope.Value;
        SharedTestAppHost.CurrentScope.Value = scope;
        try { return inner.GetService(serviceType); }
        finally { SharedTestAppHost.CurrentScope.Value = previous; }
    }

    private sealed class ScopedTestServiceScopeFactory(IServiceScopeFactory innerFactory, TestAppScope scope) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var previous = SharedTestAppHost.CurrentScope.Value;
            SharedTestAppHost.CurrentScope.Value = scope;
            var innerScope = innerFactory.CreateScope();
            return new ScopedTestServiceScope(innerScope, scope, previous);
        }
    }

    private sealed class ScopedTestServiceScope(IServiceScope innerScope, TestAppScope scope, TestAppScope? previous) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new BoundServiceProvider(innerScope.ServiceProvider, scope);

        public void Dispose()
        {
            try { innerScope.Dispose(); }
            finally { SharedTestAppHost.CurrentScope.Value = previous; }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (innerScope is IAsyncDisposable asyncDisposable)
                    return asyncDisposable.DisposeAsync();
                innerScope.Dispose();
                return ValueTask.CompletedTask;
            }
            finally
            {
                SharedTestAppHost.CurrentScope.Value = previous;
            }
        }
    }

    private sealed class BoundServiceProvider(IServiceProvider inner, TestAppScope scope) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            SharedTestAppHost.CurrentScope.Value = scope;
            return inner.GetService(serviceType);
        }
    }
}

internal sealed class DictionaryConfigurationSource(ConcurrentDictionary<string, string?> values) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DictionaryConfigurationProvider(values);

    private sealed class DictionaryConfigurationProvider(ConcurrentDictionary<string, string?> values) : ConfigurationProvider
    {
        public override bool TryGet(string key, out string? value) => values.TryGetValue(key, out value);

        public override void Set(string key, string? value) => values[key] = value;

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var prefix = parentPath is null ? string.Empty : parentPath + ConfigurationPath.KeyDelimiter;
            var results = new List<string>();
            foreach (var key in values.Keys)
            {
                if (key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var segment = Segment(key, prefix.Length);
                    results.Add(segment);
                }
            }
            results.AddRange(earlierKeys);
            results.Sort(ConfigurationKeyComparer.Instance);
            return results;
        }

        private static string Segment(string key, int prefixLength)
        {
            var indexOf = key.IndexOf(ConfigurationPath.KeyDelimiter, prefixLength, StringComparison.OrdinalIgnoreCase);
            return indexOf < 0 ? key[prefixLength..] : key[prefixLength..indexOf];
        }
    }
}

internal sealed class AsyncLocalScopeConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new AsyncLocalScopeConfigurationProvider();

    private sealed class AsyncLocalScopeConfigurationProvider : ConfigurationProvider
    {
        public override bool TryGet(string key, out string? value)
        {
            var scope = SharedTestAppHost.CurrentScope.Value;
            if (scope is not null && scope.Values.TryGetValue(key, out value))
                return true;
            value = null;
            return false;
        }

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var scope = SharedTestAppHost.CurrentScope.Value;
            if (scope is null) return earlierKeys;
            var prefix = parentPath is null ? string.Empty : parentPath + ConfigurationPath.KeyDelimiter;
            var results = new List<string>();
            foreach (var key in scope.Values.Keys)
            {
                if (key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var indexOf = key.IndexOf(ConfigurationPath.KeyDelimiter, prefix.Length, StringComparison.OrdinalIgnoreCase);
                    results.Add(indexOf < 0 ? key[prefix.Length..] : key[prefix.Length..indexOf]);
                }
            }
            results.AddRange(earlierKeys);
            results.Sort(ConfigurationKeyComparer.Instance);
            return results;
        }
    }
}
