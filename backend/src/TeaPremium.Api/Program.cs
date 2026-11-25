using TeaPremium.Api.Middleware;
using TeaPremium.Api.Outbox;
using TeaPremium.Api.Idempotency;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure;
using TeaPremium.Infrastructure.Persistence;
using TeaPremium.Infrastructure.Iam;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Settings;
using TeaPremium.Api.Media;
using TeaPremium.Infrastructure.Media;
using TeaPremium.Infrastructure.Catalog;
using TeaPremium.Api.Catalog;
using TeaPremium.Api.Inventory;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Inventory;
using TeaPremium.Api.Crm;
using TeaPremium.Infrastructure.Crm;

// Import arguments are file paths, never deployment configuration or host options.
// Leave the ordinary API/bootstrap-owner command-line behavior unchanged.
var importCommand = args.FirstOrDefault() == "import-provinces";
var catalogSeedCommand = args.FirstOrDefault() == "seed-catalog-reference";
var builder = WebApplication.CreateBuilder(importCommand || catalogSeedCommand ? [] : args);
if (catalogSeedCommand && SeedCatalogReferenceConsole.Preflight(args, builder.Environment.EnvironmentName,
    builder.Configuration.GetConnectionString("Default")) is { } seedError)
{
    Console.Error.WriteLine(seedError); Environment.ExitCode = 2; return;
}
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer(SettingsOpenApi.TransformAsync);
    options.AddDocumentTransformer(CatalogOpenApi.TransformAsync);
    options.AddDocumentTransformer(ProductOpenApi.TransformAsync);
    options.AddDocumentTransformer(InventoryDocumentEndpoints.TransformOpenApiAsync);
    options.AddDocumentTransformer(StoreCatalogOpenApi.TransformAsync);
    options.AddDocumentTransformer(CustomerOpenApi.TransformAsync);
});
builder.Logging.ClearProviders();
// Framework scopes include RequestPath. Keep them out of stdout and log only safe fields explicitly.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = false);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
// Provider exception text/SQL can contain PII or credentials even with parameter logging disabled.
// Application middleware logs a safe event/trace ID for failures instead.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.None);
builder.Services.AddUseCasesFrom(typeof(ServiceRegistration).Assembly);
builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.AddStaffIdentity(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddAdminSecurity(builder.Configuration);
builder.Services.AddMedia(builder.Configuration, builder.Environment);
builder.Services.AddCatalog();
builder.Services.AddCrm();
builder.Services.AddScoped<TeaPremium.Application.Catalog.IStoreCatalogRead, EfStoreCatalogRead>();
builder.Services.AddScoped<IInventoryStockStore, EfInventoryStockStore>();
builder.Services.AddScoped<IInventoryQueries, EfInventoryQueries>();
builder.Services.AddScoped<InventoryMutation>();
builder.Services.AddScoped<IInventoryDocumentStore, EfInventoryDocumentStore>();
builder.Services.AddScoped<InventoryDocuments>();
builder.Services.Configure<OutboxDispatcherOptions>(builder.Configuration.GetSection("OutboxDispatcher"));
builder.Services.AddHostedService<OutboxDispatcher>();
// Scaffold/OpenAPI generation can start without a DB. Production supplies this setting.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
{
    builder.Services.AddHostedService<PurgeIdempotencyKeys>();
    builder.Services.AddSingleton<ReconcileInventory>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ReconcileInventory>());
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, HttpAuditContext>();
var app = builder.Build();

if (catalogSeedCommand)
{
    Environment.ExitCode = await SeedCatalogReferenceConsole.RunAsync(app.Services);
    return;
}

if (args is ["bootstrap-owner"])
{
    Environment.ExitCode = await BootstrapOwnerConsole.RunAsync(app.Services);
    return;
}

if (importCommand)
{
    Environment.ExitCode = await TeaPremium.Api.AdministrativeData.ImportProvincesConsole.RunAsync(app.Services, args);
    return;
}

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("CI"))
    app.MapOpenApi();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ProblemExceptionMiddleware>();
if (builder.Configuration.GetSection("Iam:TrustedProxies").Get<string[]>() is { Length: > 0 })
    app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("CI")) app.UseHsts();
app.UseMiddleware<AdminTransportMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AdminCsrfMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<IdempotencyRequestBufferingMiddleware>();
app.MapStaffIam();
app.MapSettings();
app.MapMedia();
app.MapCatalogReference();
app.MapCatalog();
app.MapProducts();
app.MapSalePrices();
app.MapInventory();
app.MapInventoryDocuments();
app.MapStoreCatalog();
app.MapCustomers();

app.Run();

public partial class Program;
