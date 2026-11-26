using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class EfInteractionStore(
    AppDbContext db,
    IStaffDirectory staffDirectory,
    ICustomerOrderQueries orderQueries) : IInteractionStore
{
    private readonly Dictionary<Guid, Customer> _lockedCustomers = [];
    private readonly Dictionary<(Guid CustomerId, Guid InteractionId), Interaction> _lockedInteractions = [];

    public async Task<CustomerRecord?> LockCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var row = (await db.Set<Customer>()
            .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = {customerId} FOR NO KEY UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        _lockedCustomers[customerId] = row;
        return ToCustomerRecord(row);
    }

    public async Task<InteractionRecord?> LockInteractionAsync(Guid customerId, Guid interactionId, CancellationToken ct)
    {
        var row = (await db.Set<Interaction>()
            .FromSqlInterpolated($"SELECT * FROM crm.interactions WHERE id = {interactionId} AND customer_id = {customerId} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        _lockedInteractions[(customerId, interactionId)] = row;
        return ToInteractionRecord(row);
    }

    public async Task<Result<CustomerInteractionView>> CreateAsync(InteractionRecord interaction, DateTime nowUtc, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Interaction creation requires a transaction.");
        var customer = _lockedCustomers[interaction.CustomerId];

        var entity = new Interaction
        {
            Id = interaction.Id,
            CustomerId = interaction.CustomerId,
            StaffId = interaction.StaffId,
            Channel = interaction.Channel,
            Kind = interaction.Kind,
            Content = interaction.Content,
            OccurredAt = interaction.OccurredAt,
            OrderId = interaction.OrderId,
            CampaignId = interaction.CampaignId,
            FollowUpAt = interaction.FollowUpAt,
            FollowUpDoneAt = interaction.FollowUpDoneAt,
            FollowUpResult = interaction.FollowUpResult,
            CreatedAt = interaction.CreatedAt
        };
        db.Add(entity);

        // BR-CRM-07 (ASSUMPTION-P204-02 = A): update customers.last_contacted_at using GREATEST for non-note interactions.
        // Leave last_contacted_at, updated_at and contact_status untouched for kind='note' (and never alter contact_status).
        if (InteractionRules.CountsTowardLastContacted(entity.Kind))
        {
            var nextLastContacted = InteractionRules.MaxLastContacted(customer.LastContactedAt, entity.OccurredAt);
            if (nextLastContacted != customer.LastContactedAt)
            {
                customer.LastContactedAt = nextLastContacted;
                customer.UpdatedAt = nowUtc;
            }
        }

        const string savepoint = "p204_create_interaction";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex, "fk_interactions_order"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerInteractionView>(OrderNotFoundError());
        }

        var persisted = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == entity.Id, ct);
        return Result.Success(await BuildViewAsync(persisted, ct));
    }

    public async Task<Result<CustomerInteractionView>> UpdateAsync(
        InteractionRecord interaction,
        bool recomputeLastContacted,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Interaction update requires a transaction.");
        var customer = _lockedCustomers[interaction.CustomerId];
        var entity = _lockedInteractions[(interaction.CustomerId, interaction.Id)];

        entity.Channel = interaction.Channel;
        entity.Kind = interaction.Kind;
        entity.Content = interaction.Content;
        entity.OccurredAt = interaction.OccurredAt;
        entity.OrderId = interaction.OrderId;
        entity.FollowUpAt = interaction.FollowUpAt;

        // BR-CRM-07 (ASSUMPTION-P204-02 = A): recompute exact MAX(occurred_at) WHERE kind <> 'note'
        // (naturally including system campaign_message rows) under the parent customer lock.
        if (recomputeLastContacted)
        {
            var otherMax = await db.Set<Interaction>().AsNoTracking()
                .Where(x => x.CustomerId == interaction.CustomerId
                    && x.Id != entity.Id
                    && x.Kind != InteractionRules.NoteKind)
                .MaxAsync(x => (DateTime?)x.OccurredAt, ct);

            var recomputed = InteractionRules.CountsTowardLastContacted(entity.Kind)
                ? InteractionRules.MaxLastContacted(otherMax, entity.OccurredAt)
                : otherMax;

            if (customer.LastContactedAt != recomputed)
            {
                customer.LastContactedAt = recomputed;
                customer.UpdatedAt = nowUtc;
            }
        }

        const string savepoint = "p204_update_interaction";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex, "fk_interactions_order"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerInteractionView>(OrderNotFoundError());
        }

        var persisted = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == entity.Id, ct);
        return Result.Success(await BuildViewAsync(persisted, ct));
    }

    public async Task<Result<CustomerInteractionView>> CompleteFollowUpAsync(
        Guid customerId,
        Guid interactionId,
        DateTime doneAtUtc,
        string result,
        CancellationToken ct)
    {
        _ = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Follow-up completion requires a transaction.");
        var entity = _lockedInteractions[(customerId, interactionId)];

        // ASSUMPTION-P204-02 = A: completing a follow-up records follow_up_done_at and follow_up_result only;
        // it never mutates customers.last_contacted_at.
        entity.FollowUpDoneAt = doneAtUtc;
        entity.FollowUpResult = result;

        await db.SaveChangesAsync(ct);

        var persisted = await db.Set<Interaction>().AsNoTracking().SingleAsync(x => x.Id == entity.Id, ct);
        return Result.Success(await BuildViewAsync(persisted, ct));
    }

    private async Task<CustomerInteractionView> BuildViewAsync(Interaction row, CancellationToken ct)
    {
        var staffNames = await staffDirectory.ResolveDisplayNamesAsync([row.StaffId], ct);
        staffNames.TryGetValue(row.StaffId, out var staffDisplayName);

        string? orderCode = null;
        if (row.OrderId is { } orderId)
        {
            var orderCodes = await orderQueries.ResolveOrderCodesAsync([orderId], ct);
            orderCodes.TryGetValue(orderId, out orderCode);
        }

        return new CustomerInteractionView(
            row.Id,
            row.CustomerId,
            row.StaffId,
            staffDisplayName,
            row.Channel,
            row.Kind,
            row.Content,
            row.OccurredAt,
            row.OrderId,
            orderCode,
            row.CampaignId,
            row.FollowUpAt,
            row.FollowUpDoneAt,
            row.FollowUpResult,
            row.CreatedAt);
    }

    private static bool IsForeignKeyViolation(DbUpdateException ex, string constraintName)
        => ex.InnerException is PostgresException pg
           && pg.SqlState == PostgresErrorCodes.ForeignKeyViolation
           && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);

    private static Error OrderNotFoundError()
        => new("validation_failed", "Không tìm thấy đơn hàng.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["orderId"] = ["order_not_found"] });

    private static CustomerRecord ToCustomerRecord(Customer row) => new()
    {
        Id = row.Id,
        Code = row.Code,
        FullName = row.FullName,
        PhoneE164 = row.PhoneE164,
        Email = row.Email,
        ZaloName = row.ZaloName,
        CustomerGroup = row.CustomerGroup,
        IsVip = row.IsVip,
        Origin = row.Origin,
        AcquisitionSource = row.AcquisitionSource,
        ReferredByCustomerId = row.ReferredByCustomerId,
        ContactStatus = row.ContactStatus,
        NoteSummary = row.NoteSummary,
        HadLegacyPurchase = row.HadLegacyPurchase,
        FirstDeliveredAt = row.FirstDeliveredAt,
        LastDeliveredAt = row.LastDeliveredAt,
        DeliveredOrdersCount = row.DeliveredOrdersCount,
        LifetimeValueVnd = row.LifetimeValueVnd,
        LastContactedAt = row.LastContactedAt,
        SearchText = row.SearchText,
        MergedIntoCustomerId = row.MergedIntoCustomerId,
        ErasedAt = row.ErasedAt,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        Version = row.Version
    };

    private static InteractionRecord ToInteractionRecord(Interaction row) => new()
    {
        Id = row.Id,
        CustomerId = row.CustomerId,
        StaffId = row.StaffId,
        Channel = row.Channel,
        Kind = row.Kind,
        Content = row.Content,
        OccurredAt = row.OccurredAt,
        OrderId = row.OrderId,
        CampaignId = row.CampaignId,
        FollowUpAt = row.FollowUpAt,
        FollowUpDoneAt = row.FollowUpDoneAt,
        FollowUpResult = row.FollowUpResult,
        CreatedAt = row.CreatedAt
    };
}
