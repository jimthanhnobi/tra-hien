using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TeaPremium.Application.Iam;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace TeaPremium.IntegrationTests;

internal sealed record SeededP009Database(
    string OwnerConnection,
    Guid OwnerId,
    string OwnerStamp,
    string CertificatePath,
    X509Certificate2 Certificate);

internal sealed partial class SharedPostgresServer
{
    public const string EmptyKey = "__empty__";
    public const string LatestKey = "__latest__";
    public const string P009Key = "__p009__";

    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly SemaphoreSlim TemplateLock = new(1, 1);
    private static readonly SemaphoreSlim CloneLock = new(1, 1);
    private static readonly SemaphoreSlim HostLock = new(1, 1);
    private static readonly Lazy<IModel> SharedRuntimeModel = new(() =>
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=model_only;Username=tp_app;Password=unused")
            .Options;
        using var db = new AppDbContext(options);
        return db.Model;
    });
    private static SharedPostgresServer? _instance;

    public static IModel SharedModel => SharedRuntimeModel.Value;

    private readonly PostgreSqlContainer _container;
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);
    private SharedTestAppHost? _sharedHost;
    private int _targetSequence;
    private Guid _p009OwnerId;
    private string _p009OwnerStamp = "";

    public string AdminConnection { get; }
    public string CertificatePath { get; }
    public X509Certificate2 Certificate { get; }

    private SharedPostgresServer(PostgreSqlContainer container, string certificatePath, X509Certificate2 certificate)
    {
        _container = container;
        AdminConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        CertificatePath = certificatePath;
        Certificate = certificate;
    }

    public static async Task<SharedPostgresServer> GetAsync()
    {
        if (_instance is not null) return _instance;
        await InitLock.WaitAsync();
        try
        {
            if (_instance is not null) return _instance;
            var container = DatabaseProvisioning.Builder()
                .WithCommand(
                    "-c", "max_connections=400",
                    "-c", "shared_buffers=256MB",
                    "-c", "fsync=off",
                    "-c", "synchronous_commit=off",
                    "-c", "full_page_writes=off")
                .Build();
            await container.StartAsync();

            var certPath = Path.Combine(Path.GetTempPath(), "teapremium_shared_" + Guid.NewGuid().ToString("N") + ".pfx");
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=TeaPremium disposable test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var created = request.CreateSelfSigned(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
                await File.WriteAllBytesAsync(certPath, created.Export(X509ContentType.Pfx));
            }
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, null);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { File.Delete(certPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };

            _instance = new SharedPostgresServer(container, certPath, certificate);
            return _instance;
        }
        finally
        {
            InitLock.Release();
        }
    }

    public async Task<string> CloneAsync(string templateKey, string database)
    {
        ValidateIdentifier(database);
        var templateName = await EnsureTemplateAsync(templateKey);
        return await CloneFromTemplateNameAsync(templateName, database);
    }

    public async Task<SeededP009Database> CloneP009Async(string database)
    {
        ValidateIdentifier(database);
        var templateName = await EnsureTemplateAsync(P009Key);
        var ownerConnection = await CloneFromTemplateNameAsync(templateName, database);
        return new SeededP009Database(ownerConnection, _p009OwnerId, _p009OwnerStamp, CertificatePath, Certificate);
    }

    public async Task<SharedTestAppHost> GetSharedAppHostAsync()
    {
        if (_sharedHost is not null) return _sharedHost;
        await HostLock.WaitAsync();
        try
        {
            if (_sharedHost is not null) return _sharedHost;
            var templateName = await EnsureTemplateAsync(P009Key);
            var keyringOwner = await CloneFromTemplateNameAsync(templateName, "teapremium_test_keyring");
            var keyringRuntime = DatabaseProvisioning.Login(keyringOwner, "tp_app");
            _sharedHost = new SharedTestAppHost(this, keyringRuntime);
            return _sharedHost;
        }
        finally
        {
            HostLock.Release();
        }
    }

    public Task DropDatabaseAsync(string database)
    {
        ValidateIdentifier(database);
        // Only release pooled client connections; avoid DROP DATABASE during active
        // parallel test execution because DropDatabaseBuffers stalls concurrent queries.
        // Testcontainers Ryuk reaps the disposable server container on process exit.
        ClearPools(database);
        return Task.CompletedTask;
    }

    public async Task<bool> ObserveSleepingAsync(string database)
    {
        var admin = new NpgsqlConnectionStringBuilder(AdminConnection) { Database = database };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep')",
            connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> EnsureTemplateAsync(string templateKey)
    {
        await TemplateLock.WaitAsync();
        try
        {
            return await EnsureTemplateCoreAsync(templateKey);
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    private async Task<string> EnsureTemplateCoreAsync(string templateKey)
    {
        if (_templates.TryGetValue(templateKey, out var existing))
            return existing;

        string templateName;
        if (templateKey == EmptyKey)
        {
            templateName = "tp_tpl_empty";
            await DatabaseProvisioning.ProvisionAsync(_container, templateName);
        }
        else if (templateKey == LatestKey)
        {
            var empty = await EnsureTemplateCoreAsync(EmptyKey);
            templateName = "tp_tpl_latest";
            var owner = await CloneFromTemplateNameAsync(empty, templateName);
            await DatabaseProvisioning.MigrateAsync(owner);
        }
        else if (templateKey == P009Key)
        {
            var latest = await EnsureTemplateCoreAsync(LatestKey);
            templateName = "tp_tpl_p009";
            var owner = await CloneFromTemplateNameAsync(latest, templateName);
            await SeedP009TemplateAsync(owner);
        }
        else
        {
            var empty = await EnsureTemplateCoreAsync(EmptyKey);
            templateName = $"tp_tpl_m_{++_targetSequence}";
            var owner = await CloneFromTemplateNameAsync(empty, templateName);
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner).Options);
            await db.GetService<IMigrator>().MigrateAsync(templateKey);
        }

        await DetachTemplateConnectionsAsync(templateName);
        _templates[templateKey] = templateName;
        return templateName;
    }

    private async Task SeedP009TemplateAsync(string ownerConnection)
    {
        var runtime = DatabaseProvisioning.Login(ownerConnection, "tp_app");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = runtime,
            ["Iam:KeyCertificatePath"] = CertificatePath
        }).Build();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(config));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);
                services.AddDbContext<AppDbContext>(options => options.UseModel(SharedModel));
                services.AddDataProtection().SetApplicationName("teapremium").PersistKeysToDbContext<AppDbContext>()
                    .ProtectKeysWithCertificate(Certificate);
            });
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var bootstrap = await scope.ServiceProvider.GetRequiredService<ICommandHandler<BootstrapOwner, bool>>()
            .Handle(new("owner@example.test", P009Harness.Password + "-Initial"), CancellationToken.None);
        Assert.True(bootstrap.IsSuccess && bootstrap.Value);
        var staff = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .SingleAsync(x => x.Email == "owner@example.test");
        _p009OwnerId = staff.Id;
        var changed = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>()
            .Handle(new(staff.Id, P009Harness.Password + "-Initial", P009Harness.Password), CancellationToken.None);
        Assert.True(changed.IsSuccess);
        _p009OwnerStamp = (await scope.ServiceProvider.GetRequiredService<IStaffIdentity>()
            .SessionAsync(staff.Id, CancellationToken.None))!.SecurityStamp;
        // Materialize the shared Data Protection key in the template DB once.
        scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("teapremium.template.warmup").Protect("ok");
    }

    private async Task<string> CloneFromTemplateNameAsync(string templateName, string database)
    {
        await CloneLock.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(AdminConnection);
            await connection.OpenAsync();
            await using (var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{database}\" TEMPLATE \"{templateName}\" STRATEGY = FILE_COPY OWNER tp_owner", connection))
            {
                await create.ExecuteNonQueryAsync();
            }

            // PostgreSQL does not copy database-level ACL from templates; mirror init-roles.sql.
            await using var grants = new NpgsqlCommand(
                $"REVOKE ALL ON DATABASE \"{database}\" FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO tp_app, tp_readonly;",
                connection);
            await grants.ExecuteNonQueryAsync();
        }
        finally
        {
            CloneLock.Release();
        }

        return DatabaseProvisioning.Login(AdminConnection, "tp_owner", database);
    }

    private async Task DetachTemplateConnectionsAsync(string database)
    {
        ClearPools(database);
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
            connection);
        command.Parameters.AddWithValue("name", database);
        await command.ExecuteNonQueryAsync();
    }

    private void ClearPools(string database)
    {
        foreach (var role in new[] { "tp_owner", "tp_app", "tp_readonly" })
        {
            using var conn = new NpgsqlConnection(DatabaseProvisioning.Login(AdminConnection, role, database));
            NpgsqlConnection.ClearPool(conn);
        }
        using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(AdminConnection) { Database = database }.ConnectionString);
        NpgsqlConnection.ClearPool(admin);
    }

    private static void ValidateIdentifier(string name)
    {
        if (!IdentifierPattern().IsMatch(name))
            throw new ArgumentException($"Invalid test database identifier: {name}", nameof(name));
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
