namespace TeaPremium.Infrastructure.Persistence;

public interface IAuditContext
{
    string ActorType { get; }
    Guid? ActorId { get; }
    string? CorrelationId { get; }
}

public sealed class SystemAuditContext : IAuditContext
{
    public string ActorType => "system";
    public Guid? ActorId => null;
    public string? CorrelationId => null;
}
