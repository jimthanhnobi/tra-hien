using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Time;

namespace TeaPremium.Api.Iam;

public static class AdminSecurity
{
    public const string Scheme = "AdminCookie";
    public const string Onboarding = "StaffOnboarding";
    public const string Ready = "StaffReady";
    internal const string Stamp = "staff_stamp";
    internal const string Started = "staff_started";
    internal const string Factor = "staff_factor";

    public static IServiceCollection AddAdminSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "__Host-tp_admin";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = ValidateAsync,
                OnRedirectToLogin = context => Problem(context.HttpContext, 401, "unauthenticated").ExecuteAsync(context.HttpContext),
                OnRedirectToAccessDenied = context => Problem(context.HttpContext, 403, "forbidden").ExecuteAsync(context.HttpContext)
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<TimeProvider>((options, time) => options.TimeProvider = time);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Onboarding, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
            options.AddPolicy(Ready, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim("staff_ready", "true"));
            foreach (var permission in StaffPermissions.ByRole["owner"])
                options.AddPolicy(permission, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
                    .RequireClaim("staff_ready", "true").RequireClaim("permission", permission));
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminAuthorizationResultHandler>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-tp_admin_csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.ForwardLimit = 1;
            // Empty means forwarding is disabled below, NEVER trust all proxies.
            foreach (var value in configuration.GetSection("Iam:TrustedProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(System.Net.IPAddress.Parse(value));
        });
        services.AddRateLimiter(options =>
        {
            options.AddConcurrencyLimiter("media", policy => { policy.PermitLimit = 2; policy.QueueLimit = 0; });
            options.AddPolicy("staff-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = (context, _) => new ValueTask(Problem(context.HttpContext, 429, "rate_limited").ExecuteAsync(context.HttpContext));
        });
        return services;
    }

    internal static ClaimsPrincipal Principal(StaffView user, string stamp, bool factor, DateTimeOffset started, bool production)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(Stamp, stamp),
            new(Started, started.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)), new(Factor, factor ? "true" : "false")
        };
        claims.AddRange(user.Roles.Select(x => new Claim(ClaimTypes.Role, x)));
        var requiredFactor = user.TotpEnabled || production && user.Roles.Contains("owner", StringComparer.Ordinal);
        if (!user.MustChangePassword && (!requiredFactor || user.TotpEnabled && factor))
        {
            claims.Add(new Claim("staff_ready", "true"));
            claims.AddRange(user.Permissions.Select(x => new Claim("permission", x)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var principal = context.Principal;
        var now = services.GetRequiredService<IClock>().UtcNow;
        if (context.Properties.ExpiresUtc <= now
            || !Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            || !long.TryParse(principal.FindFirstValue(Started), CultureInfo.InvariantCulture, out var started)
            || now.ToUnixTimeSeconds() < started || now.ToUnixTimeSeconds() - started >= 7 * 86400)
        {
            context.RejectPrincipal();
            return;
        }
        var session = await services.GetRequiredService<IStaffIdentity>().SessionAsync(id, context.HttpContext.RequestAborted);
        if (session is null || session.SecurityStamp != principal.FindFirstValue(Stamp))
        {
            context.RejectPrincipal();
            return;
        }
        context.ReplacePrincipal(Principal(session.User, session.SecurityStamp, principal.FindFirstValue(Factor) == "true",
            DateTimeOffset.FromUnixTimeSeconds(started), services.GetRequiredService<IHostEnvironment>().IsProduction()));
    }

    public static IResult Problem(HttpContext context, int status, string code) => Results.Problem(
        statusCode: status, type: $"https://teapremium/errors/{code}", title: "Yêu cầu không được chấp nhận.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier });
}

internal sealed class AdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        => context.Request.Path.StartsWithSegments("/api/v1/admin") && !authorizeResult.Succeeded
            ? AdminSecurity.Problem(context, authorizeResult.Challenged ? 401 : 403, authorizeResult.Challenged ? "unauthenticated" : "forbidden").ExecuteAsync(context)
            : _fallback.HandleAsync(next, context, policy, authorizeResult);
}

public sealed class AdminTransportMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps)
            {
                await AdminSecurity.Problem(context, 400, "https_required").ExecuteAsync(context);
                return;
            }
            var host = configuration["Iam:AdminHost"];
            if (!string.IsNullOrWhiteSpace(host) && !string.Equals(host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
            {
                await AdminSecurity.Problem(context, 403, "forbidden").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}

// Runs AFTER authorization and BEFORE binding, idempotency filters, or any mutation.
public sealed class AdminCsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/admin")
            && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await AdminSecurity.Problem(context, 400, "csrf_invalid").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
