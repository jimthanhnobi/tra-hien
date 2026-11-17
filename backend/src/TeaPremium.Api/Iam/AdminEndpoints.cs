using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Iam;

public sealed record LoginRequest(string Email, string Password, string? TotpCode);
public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
public sealed record TotpRequest(string Code);
public sealed record CreateStaffRequest(string Email, string DisplayName, string InitialPassword, string[] Roles);
public sealed record PatchStaffRequest(string RowVersion, bool? IsActive, bool? Locked, string[]? Roles, string? NewPassword);
public sealed record CsrfResponse(string Token);
public sealed record AuthResponse(StaffView User, bool PasswordChangeRequired, bool TotpRequired);

public static class AdminEndpoints
{
    public static void MapStaffIam(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/admin/auth").WithTags("Staff authentication");
        auth.MapGet("/csrf", (HttpContext http, IAntiforgery csrf) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new CsrfResponse(csrf.GetAndStoreTokens(http).RequestToken!));
        }).AllowAnonymous();
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("staff-login")
            .Produces<AuthResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(429);
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AdminSecurity.Scheme);
            return Results.NoContent();
        }).RequireAuthorization(AdminSecurity.Onboarding).Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        auth.MapGet("/me", async (HttpContext http, IStaffIdentity identity, IHostEnvironment environment, CancellationToken ct) =>
        {
            var user = await identity.FindAsync(Actor(http), ct);
            return user is null ? AdminSecurity.Problem(http, 401, "unauthenticated") : Results.Ok(Response(user, http.User, environment));
        }).RequireAuthorization(AdminSecurity.Onboarding).Produces<AuthResponse>();
        auth.MapPost("/change-password", async (PasswordRequest request, HttpContext http,
            ICommandHandler<ChangeStaffPassword, bool> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(Actor(http), request.CurrentPassword, request.NewPassword), ct);
            if (!result.IsSuccess) return result.Error.ToProblem(http);
            if (!result.Value) return AdminSecurity.Problem(http, 400, "validation_failed");
            await http.SignOutAsync(AdminSecurity.Scheme);
            return Results.NoContent();
        }).RequireAuthorization(AdminSecurity.Onboarding).Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        auth.MapPost("/totp/setup", async (HttpContext http, ICommandHandler<SetupStaffTotp, TotpSetup> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(Actor(http)), ct);
            return Map(result, http);
        }).RequireAuthorization(AdminSecurity.Onboarding).Produces<TotpSetup>();
        auth.MapPost("/totp/verify", async (TotpRequest request, HttpContext http,
            ICommandHandler<VerifyStaffTotp, bool> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(Actor(http), request.Code), ct);
            if (!result.IsSuccess) return result.Error.ToProblem(http);
            if (!result.Value) return AdminSecurity.Problem(http, 400, "totp_invalid");
            await http.SignOutAsync(AdminSecurity.Scheme);
            return Results.NoContent();
        }).RequireAuthorization(AdminSecurity.Onboarding).Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        var staff = endpoints.MapGroup("/api/v1/admin/staff").WithTags("Staff management").RequireAuthorization("staff.manage");
        staff.MapGet("/", async (int? page, int? pageSize, HttpContext http, IStaffIdentity identity, CancellationToken ct) =>
        {
            var number = page ?? 1;
            var size = pageSize ?? 20;
            if (number is < 1 or > 1000000 || size is < 1 or > 100) return AdminSecurity.Problem(http, 400, "validation_failed");
            return Results.Ok(await identity.ListAsync(number, size, ct));
        }).Produces<StaffPage>();
        staff.MapGet("/{id:guid}", async (Guid id, HttpContext http, IStaffIdentity identity, CancellationToken ct) =>
        {
            var user = await identity.FindAsync(id, ct);
            return user is null ? AdminSecurity.Problem(http, 404, "not_found") : Results.Ok(user);
        }).Produces<StaffView>();
        staff.MapPost("/", async (CreateStaffRequest request, HttpContext http,
            ICommandHandler<CreateStaff, StaffView> handler, CancellationToken ct)
            => Map(await handler.Handle(new(request.Email, request.DisplayName, request.InitialPassword, request.Roles,
                Actor(http), http.User.FindFirstValue(AdminSecurity.Stamp)!), ct), http))
            .Produces<StaffView>();
        staff.MapPatch("/{id:guid}", async (Guid id, PatchStaffRequest request, HttpContext http,
            ICommandHandler<PatchStaff, StaffView> handler, CancellationToken ct)
            => Map(await handler.Handle(new(id, request.RowVersion, request.IsActive, request.Locked, request.Roles, request.NewPassword,
                Actor(http), http.User.FindFirstValue(AdminSecurity.Stamp)!), ct), http))
            .Produces<StaffView>();
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, IHostEnvironment environment,
        IClock clock, ICommandHandler<LoginStaff, LoginOutcome> handler, CancellationToken ct)
    {
        var result = await handler.Handle(new(request.Email, request.Password, request.TotpCode), ct);
        if (!result.IsSuccess) return result.Error.ToProblem(http);
        var outcome = result.Value;
        if (outcome.User is null) return AdminSecurity.Problem(http, 401, "invalid_credentials");
        var principal = AdminSecurity.Principal(outcome.User, outcome.SecurityStamp!, outcome.FactorComplete, clock.UtcNow, environment.IsProduction());
        await http.SignInAsync(AdminSecurity.Scheme, principal, new AuthenticationProperties { IsPersistent = false });
        return Results.Ok(Response(outcome.User, principal, environment));
    }

    private static AuthResponse Response(StaffView user, ClaimsPrincipal principal, IHostEnvironment environment)
        => new(user with { Permissions = principal.FindAll("permission").Select(x => x.Value).ToArray() }, user.MustChangePassword,
            environment.IsProduction() && user.Roles.Contains("owner", StringComparer.Ordinal) && !user.TotpEnabled);

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Map<T>(Result<T> result, HttpContext http) => result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
}
