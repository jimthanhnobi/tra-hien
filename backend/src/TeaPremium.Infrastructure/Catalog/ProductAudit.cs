using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

// B01: targeted business audit, not a logger for every content edit. Search-only SQL does not bypass an [Audited] entity.
public sealed class ProductAudit(AppDbContext db, IAuditContext actor, IClock clock, IDataProtectionProvider protection)
{
    public void Record(ProductData? before, ProductData after, string? reason)
    {
        if (before is not null && before.Status == after.Status && before.RobotsNoindex == after.RobotsNoindex && before.DescriptionWordCount == after.DescriptionWordCount) return;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityType = "catalog.products",
            EntityId = after.Id,
            Action = "state_change",
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = actor.CorrelationId,
            Changes = JsonSerializer.Serialize(new
            {
                status = new { before = before?.Status, after = after.Status },
                robots_noindex = new { before = before?.RobotsNoindex, after = after.RobotsNoindex },
                description_word_count = new { before = before?.DescriptionWordCount, after = after.DescriptionWordCount },
                thin_description = new { before = before?.ThinDescription, after = after.ThinDescription },
                reason = string.IsNullOrWhiteSpace(reason) ? null : protection.CreateProtector("teapremium.catalog.noindex-reason.v1").Protect(reason),
                reason_format = string.IsNullOrWhiteSpace(reason) ? null : "data_protection_v1"
            })
        });
    }
}
