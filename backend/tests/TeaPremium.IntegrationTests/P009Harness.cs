using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Settings;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class P009Harness : IAsyncDisposable
{
    public const string Password = "Disposable-Owner-P009!";
    public required string OwnerConnection { get; init; }
    public required string RuntimeConnection { get; init; }
    public required string Database { get; init; }
    public required string CertificatePath { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required HttpClient Client { get; init; }
    public required IConfigurationRoot Configuration { get; init; }
    public Guid OwnerId { get; private set; }
    public string OwnerStamp { get; private set; } = "";

    public static async Task<P009Harness> CreateAsync(PlatformDatabaseFixture fixture, Action<IServiceCollection>? configureServices = null)
    {
        var database = "teapremium_test_" + Guid.NewGuid().ToString("N");
        var owner = await fixture.CreateDatabaseAsync(database);
        var runtime = new NpgsqlConnectionStringBuilder(owner) { Username = "tp_app", Password = "Disposable-Test-Only!" }.ConnectionString;
        var certPath = Path.Combine(Path.GetTempPath(), database + ".pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=P009 disposable test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await File.WriteAllBytesAsync(certPath, cert.Export(X509ContentType.Pfx));
        }
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, null);
        var descriptor = new NpgsqlConnectionStringBuilder(runtime);
        var target = SettingsJson.Serialize(new object[] { descriptor.Host!, descriptor.Port, database, "Test" });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = runtime,
            ["Iam:KeyCertificatePath"] = certPath,
            ["AdministrativeImport:AllowedTargets:0"] = target
        }).Build();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(config));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                // Minimal-host test configuration arrives after Program's registrations.
                // Explicitly share the production key repository/certificate with real CLI processes.
                services.AddDataProtection().SetApplicationName("teapremium").PersistKeysToDbContext<AppDbContext>()
                    .ProtectKeysWithCertificate(certificate);
                configureServices?.Invoke(services);
            });
        });
        var result = new P009Harness
        {
            Database = database,
            OwnerConnection = owner,
            RuntimeConnection = runtime,
            CertificatePath = certPath,
            Certificate = certificate,
            Factory = factory,
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }),
            Configuration = config
        };
        await using var scope = factory.Services.CreateAsyncScope();
        var bootstrap = await scope.ServiceProvider.GetRequiredService<ICommandHandler<BootstrapOwner, bool>>().Handle(new("owner@example.test", Password + "-Initial"), default);
        Assert.True(bootstrap.IsSuccess && bootstrap.Value);
        var staff = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Email == "owner@example.test");
        result.OwnerId = staff.Id;
        var changed = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ChangeStaffPassword, bool>>().Handle(new(staff.Id, Password + "-Initial", Password), default);
        Assert.True(changed.IsSuccess);
        result.OwnerStamp = (await scope.ServiceProvider.GetRequiredService<IStaffIdentity>().SessionAsync(staff.Id, default))!.SecurityStamp;
        return result;
    }
    public async Task LoginAsync(string email = "owner@example.test", string password = Password)
    {
        using var response = await MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/login", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
    public async Task<HttpResponseMessage> MutateAsync(HttpMethod method, string path, object body)
    {
        using var csrf = await Client.GetAsync("/api/v1/admin/auth/csrf");
        var token = (await csrf.Content.ReadFromJsonAsync<TeaPremium.Api.Iam.CsrfResponse>())!.Token;
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("X-Correlation-ID", "3cfd2f4d-8d0b-4fc5-b39e-7c81701a221d");
        return await Client.SendAsync(request);
    }
    public void Trust(ImportBundle bundle) => Configuration["AdministrativeImport:TrustedManifestSha256:" + SettingsJson.Hash(bundle.ManifestBytes)] = SettingsJson.Hash(bundle.ManifestBytes);
    public async Task<ImportPlan> PlanAsync(ImportBundle bundle)
    {
        Trust(bundle);
        await using var scope = Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueryHandler<PlanAdministrativeImport, ImportPlan>>().Handle(new(bundle), default);
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        return result.Value;
    }
    public ImportExecution Approve(ImportBundle bundle, ImportPlan plan)
    {
        var m = bundle.Manifest();
        string[] Codes(string kind, string action) => plan.Changes.Where(x => (kind == "*" || x.Kind == kind) && x.Action == action).Select(x => x.Code).Order(StringComparer.Ordinal).ToArray();
        var approval = new ImportApproval(plan.Fingerprint(), plan.Profile, plan.Target, OwnerId, DateTimeOffset.UtcNow,
            Codes("province", "deactivate"), Codes("ward", "deactivate"), Codes("ward", "reparent"), Codes("*", "reactivate"), null, null,
            m.RecoveryEventId, m.ReAdvanceOf, m.PathPosition);
        var hash = SettingsJson.Hash(ImportJson.Write(approval));
        Configuration["AdministrativeImport:TrustedApprovalSha256:" + hash] = hash;
        return new(plan, approval, hash, OwnerId, false, OwnerStamp);
    }
    public async Task<TeaPremium.Application.Results.Result<ImportOutcome>> ApplyAsync(ImportBundle bundle, ImportExecution execution)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyAdministrativeImport, ImportOutcome>>().Handle(new(bundle, execution), default);
    }
    public AppDbContext OwnerContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(OwnerConnection).Options);
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await Factory.DisposeAsync(); Certificate.Dispose(); File.Delete(CertificatePath); (Configuration as IDisposable)?.Dispose();
    }
}
