using System.Security.Claims;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Api.Middleware;

public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public string ActorType => ResolveActor(accessor.HttpContext?.User).Type;
    public Guid? ActorId => ResolveActor(accessor.HttpContext?.User).Id;
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    private static (string Type, Guid? Id) ResolveActor(ClaimsPrincipal? principal)
    {
        if (principal is null) return ("system", null);
        var verified = principal.Identities
            .Where(identity => identity.IsAuthenticated && identity.AuthenticationType is "AdminCookie" or "CustomerCookie")
            .Select(identity => (Type: identity.AuthenticationType == "AdminCookie" ? "staff" : "customer",
                Id: Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : Guid.Empty))
            .Where(actor => actor.Id != Guid.Empty)
            .ToArray();
        return verified.Length == 1 ? (verified[0].Type, verified[0].Id) : ("system", null);
    }
}
