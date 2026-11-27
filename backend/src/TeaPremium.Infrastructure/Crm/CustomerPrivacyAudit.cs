using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class CustomerPrivacyAudit(
    AppDbContext db,
    IAuditContext actor,
    IClock clock,
    IDataProtectionProvider protection)
{
    public const string EraseReasonProtectorPurpose = "teapremium.crm.erase-reason.v1";

    public void RecordMerge(CustomerMergeAudit audit)
    {
        var (actorType, actorId, correlation) = ValidateActorContext();
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityType = "crm.customers",
            EntityId = audit.TargetCustomerId,
            Action = "state_change",
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = correlation,
            Changes = JsonSerializer.Serialize(new
            {
                operation = "merge",
                merged_customer_id = audit.MergedCustomerId,
                flattened_chain_count = audit.FlattenedChainCount,
                account_transferred = audit.AccountTransferred,
                history_unlock_reset = audit.HistoryUnlockReset,
                moved_orders_count = audit.MovedOrdersCount,
                moved_interactions_count = audit.MovedInteractionsCount,
                moved_vouchers_count = audit.MovedVouchersCount,
                moved_redemptions_count = audit.MovedRedemptionsCount
            })
        });
    }

    public void RecordErase(CustomerEraseAudit audit)
    {
        var (actorType, actorId, correlation) = ValidateActorContext();
        var protectedReason = protection.CreateProtector(EraseReasonProtectorPurpose).Protect(audit.Reason);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityType = "crm.customers",
            EntityId = audit.CustomerId,
            Action = "state_change",
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = correlation,
            Changes = JsonSerializer.Serialize(new
            {
                operation = "erase",
                data_request_id = audit.DataRequestId,
                request_channel = audit.RequestChannel,
                anonymized_orders_count = audit.AnonymizedOrdersCount,
                scrubbed_interactions_count = audit.ScrubbedInteractionsCount,
                scrubbed_merged_stubs_count = audit.ScrubbedMergedStubsCount,
                account_disabled = audit.AccountDisabled,
                reason = protectedReason,
                reason_format = "data_protection_v1"
            })
        });
    }

    public void RecordExportAccess(Guid customerId)
    {
        var (actorType, actorId, correlation) = ValidateActorContext();
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityType = "crm.customers",
            EntityId = customerId,
            Action = "access",
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = clock.UtcNow.UtcDateTime,
            CorrelationId = correlation,
            Changes = JsonSerializer.Serialize(new
            {
                operation = "export_customer_data"
            })
        });
    }

    private (string ActorType, Guid? ActorId, string? CorrelationId) ValidateActorContext()
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

        return (actorType, actorId, correlation);
    }
}
