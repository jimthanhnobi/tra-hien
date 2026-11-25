using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class CustomerIdentityAudit(AppDbContext db, IAuditContext actor, IClock clock, IDataProtectionProvider protection)
{
    public const string ReasonProtectorPurpose = "teapremium.crm.correct-identity-reason.v1";

    public void Record(CustomerIdentityCorrectionAudit correction)
    {
        var actorType = actor.ActorType;
        var actorId = actor.ActorId;
        if (actorType is not ("staff" or "customer" or "system")
            || (actorType == "system" && actorId is not null)
            || (actorType != "system" && actorId is null))
        {
            throw new InvalidOperationException("Audit actor context is invalid.");
        }

        var correlation = actor.CorrelationId;
        if (correlation is not null
            && !Guid.TryParseExact(correlation, "N", out _)
            && !Guid.TryParseExact(correlation, "D", out _))
        {
            correlation = null;
        }

        var protectedReason = protection.CreateProtector(ReasonProtectorPurpose).Protect(correction.Reason);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityType = "crm.customers",
            EntityId = correction.CustomerId,
            Action = "state_change",
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = correlation,
            Changes = JsonSerializer.Serialize(new
            {
                changed_fields = correction.ChangedFields,
                phone_changed = correction.PhoneChanged,
                origin = correction.BeforeOrigin is null && correction.AfterOrigin is null
                    ? null
                    : new { before = correction.BeforeOrigin, after = correction.AfterOrigin },
                acquisition_source = correction.BeforeAcquisitionSource is null && correction.AfterAcquisitionSource is null
                    ? null
                    : new { before = correction.BeforeAcquisitionSource, after = correction.AfterAcquisitionSource },
                had_legacy_purchase = correction.BeforeHadLegacyPurchase is null && correction.AfterHadLegacyPurchase is null
                    ? null
                    : new { before = correction.BeforeHadLegacyPurchase, after = correction.AfterHadLegacyPurchase },
                reason = protectedReason,
                reason_format = "data_protection_v1"
            })
        });
    }
}
