using TeaPremium.Api.Middleware;
using TeaPremium.Api.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Infrastructure;
using TeaPremium.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Logging.ClearProviders();
// Framework scopes include RequestPath. Keep them out of stdout and log only safe fields explicitly.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = false);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddUseCasesFrom(typeof(ServiceRegistration).Assembly);
builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.Configure<OutboxDispatcherOptions>(builder.Configuration.GetSection("OutboxDispatcher"));
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, HttpAuditContext>();
var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("CI"))
    app.MapOpenApi();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ProblemExceptionMiddleware>();

app.Run();

public partial class Program;
