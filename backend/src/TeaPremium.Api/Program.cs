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

// Import arguments are file paths, never deployment configuration or host options.
// Leave the ordinary API/bootstrap-owner command-line behavior unchanged.
var importCommand = args.FirstOrDefault() == "import-provinces";
var builder = WebApplication.CreateBuilder(importCommand ? [] : args);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer(SettingsOpenApi.TransformAsync));
builder.Logging.ClearProviders();
// Framework scopes include RequestPath. Keep them out of stdout and log only safe fields explicitly.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = false);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Services.AddUseCasesFrom(typeof(ServiceRegistration).Assembly);
builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.AddStaffIdentity(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddAdminSecurity(builder.Configuration);
builder.Services.Configure<OutboxDispatcherOptions>(builder.Configuration.GetSection("OutboxDispatcher"));
builder.Services.AddHostedService<OutboxDispatcher>();
// Scaffold/OpenAPI generation can start without a DB. Production supplies this setting.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
    builder.Services.AddHostedService<PurgeIdempotencyKeys>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, HttpAuditContext>();
var app = builder.Build();

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

app.Run();

public partial class Program;
