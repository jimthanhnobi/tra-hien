using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Persistence;

// Persistence shapes for the P0-04 platform tables. Business behavior is added by later tasks.
[Audited]
public sealed class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public sealed class Province
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public ICollection<Ward> Wards { get; set; } = new List<Ward>();
}

public sealed class Ward
{
    public string Code { get; set; } = "";
    public string ProvinceCode { get; set; } = "";
    public Province Province { get; set; } = null!;
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public string ActorType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string? EntityKey { get; set; }
    public string Action { get; set; } = "";
    public string Changes { get; set; } = "{}";
    public string? CorrelationId { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public int Attempts { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class IdempotencyKey
{
    public string Scope { get; set; } = "";
    public Guid Key { get; set; }
    public string RequestHash { get; set; } = "";
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class InboundWebhook
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTime ReceivedAt { get; set; }
    public string Status { get; set; } = "received";
    public DateTime? ProcessedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class NotificationLog
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = "";
    public string Template { get; set; } = "";
    public string RecipientHash { get; set; } = "";
    public string? RefType { get; set; }
    public Guid? RefId { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public sealed class DataProtectionKey
{
    public int Id { get; set; }
    public string? FriendlyName { get; set; }
    public string? Xml { get; set; }
}
