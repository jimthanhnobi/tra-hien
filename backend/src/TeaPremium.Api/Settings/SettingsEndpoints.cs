using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Settings;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Settings;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSettingsRequest(string Version, JsonElement Values);

public static class SettingsEndpoints
{
    public static void MapSettings(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/v1/admin/settings").WithTags("Settings").RequireAuthorization(AdminSecurity.Ready);
        settings.MapGet("/{group}", async (string group, HttpContext http, IAuthorizationService authorization,
            IQueryHandler<GetSettings, SettingsGroup> handler, CancellationToken ct) =>
        {
            var permission = group == "site" ? "content.write" : "settings.write";
            if (!(await authorization.AuthorizeAsync(http.User, permission)).Succeeded) return AdminSecurity.Problem(http, 403, "forbidden");
            var result = await handler.Handle(new(group), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).Produces<SettingsGroup>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        settings.MapPut("/{group}", async (string group, UpdateSettingsRequest request, HttpContext http,
            ICommandHandler<PutSettings, SettingsGroup> handler, CancellationToken ct) =>
        {
            var actor = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await handler.Handle(new(group, request.Version, request.Values, actor), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("settings.write").Produces<SettingsGroup>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);
    }
}
