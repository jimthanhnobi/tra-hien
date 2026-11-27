using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using DomainShipment = TeaPremium.Domain.Orders.Shipment;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using PersistenceShipment = TeaPremium.Infrastructure.Persistence.Shipment;
using PersistenceShipmentEvent = TeaPremium.Infrastructure.Persistence.ShipmentEvent;

namespace TeaPremium.Infrastructure.Orders;

public sealed class EfShipmentRepository(AppDbContext db) : IShipmentRepository
{
    public Task<ShipmentOrderLookup?> FindOrderLookupAsync(Guid shipmentId, CancellationToken ct) =>
        db.Set<PersistenceShipment>().AsNoTracking()
            .Where(x => x.Id == shipmentId)
            .Select(x => new ShipmentOrderLookup(x.Id, x.OrderId, x.Purpose))
            .SingleOrDefaultAsync(ct);

    public async Task<DomainShipment?> LoadForUpdateAsync(Guid shipmentId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Loading a shipment for update requires an active transaction.");

        var row = (await db.Set<PersistenceShipment>().FromSqlInterpolated(
            $"SELECT * FROM sales.shipments WHERE id = {shipmentId} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();

        if (row is null) return null;

        var events = await LoadOrderedEventsAsync(shipmentId, ct);
        return Rehydrate(row, events);
    }

    public async Task<DomainShipment?> FindDeliveredOrderShipmentAsync(Guid orderId, CancellationToken ct)
    {
        var row = await db.Set<PersistenceShipment>().AsNoTracking()
            .Where(x => x.OrderId == orderId
                && x.Purpose == ShipmentPurpose.Order
                && x.Status == ShipmentStatus.Delivered)
            .OrderByDescending(x => x.DeliveredAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var events = await LoadOrderedEventsAsync(row.Id, ct);
        return Rehydrate(row, events);
    }

    public async Task<ShipmentWriteResult> InsertAsync(DomainShipment shipment, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Inserting a shipment requires an active transaction.");

        var row = ToPersistenceShipment(shipment);
        var newEvents = shipment.PendingNewEvents.Select(ToPersistenceEvent).ToArray();
        var prior = db.ChangeTracker.Entries()
            .Select(x => x.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        await tx.CreateSavepointAsync("shipment_insert", ct);
        db.Set<PersistenceShipment>().Add(row);
        foreach (var evt in newEvents)
            db.Set<PersistenceShipmentEvent>().Add(evt);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync("shipment_insert", ct);
            shipment.DrainNewEvents();
            return ShipmentWriteResult.Success();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: "23505",
            ConstraintName: "ux_shipment_active_order" or "ux_shipment_tracking"
        } pg)
        {
            await tx.RollbackToSavepointAsync("shipment_insert", ct);
            // Detach Shipment, ShipmentEvent(s), AND any AuditLog emitted by AuditSaveChangesInterceptor.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
            await tx.ReleaseSavepointAsync("shipment_insert", ct);

            var conflictCode = pg.ConstraintName == "ux_shipment_active_order"
                ? "active_shipment_exists"
                : "tracking_code_exists";
            return ShipmentWriteResult.Conflict(conflictCode);
        }
    }

    public async Task<ShipmentWriteResult> SaveUpdatedAsync(DomainShipment shipment, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Updating a shipment requires an active transaction.");

        var row = db.Set<PersistenceShipment>().Local.SingleOrDefault(x => x.Id == shipment.Id)
            ?? await db.Set<PersistenceShipment>().SingleAsync(x => x.Id == shipment.Id, ct);

        var prior = db.ChangeTracker.Entries()
            .Select(x => x.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        row.TrackingCode = shipment.TrackingCode;
        row.Status = shipment.Status;
        row.ShippingCostVnd = shipment.ShippingCostVnd;
        row.ReturnCostVnd = shipment.ReturnCostVnd;
        row.PickedUpAt = shipment.PickedUpAt?.UtcDateTime;
        row.DeliveredAt = shipment.DeliveredAt?.UtcDateTime;
        row.FailedAt = shipment.FailedAt?.UtcDateTime;
        row.ReturnedAt = shipment.ReturnedAt?.UtcDateTime;

        foreach (var evt in shipment.PendingNewEvents)
            db.Set<PersistenceShipmentEvent>().Add(ToPersistenceEvent(evt));

        await tx.CreateSavepointAsync("shipment_update", ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync("shipment_update", ct);
            shipment.DrainNewEvents();
            return ShipmentWriteResult.Success();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: "23505",
            ConstraintName: "ux_shipment_active_order" or "ux_shipment_tracking"
        } pg)
        {
            await tx.RollbackToSavepointAsync("shipment_update", ct);
            // Detach added ShipmentEvent(s), AuditLog(s) emitted by interceptor, and revert dirty tracked Shipment.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
            db.Entry(row).State = EntityState.Detached;
            await tx.ReleaseSavepointAsync("shipment_update", ct);

            var conflictCode = pg.ConstraintName == "ux_shipment_active_order"
                ? "active_shipment_exists"
                : "tracking_code_exists";
            return ShipmentWriteResult.Conflict(conflictCode);
        }
    }

    // Layer-2 anti-double-restock guard (G16): order-level return_restock uses ref_type='order', ref_id=orderId.
    public Task<bool> HasOrderReturnRestockMovementAsync(Guid orderId, CancellationToken ct) =>
        db.Set<StockMovement>().AsNoTracking()
            .AnyAsync(x => x.MovementType == "return_restock"
                && x.RefType == "order"
                && x.RefId == orderId, ct);

    public Task<bool> HasNonVoidedCodPaymentAsync(Guid orderId, CancellationToken ct) =>
        db.Set<Payment>().AsNoTracking()
            .AnyAsync(x => x.OrderId == orderId
                && x.Method == PaymentMethod.Cod
                && x.Status != PaymentRecordStatus.Voided, ct);

    public async Task InsertAwaitingCodPaymentAsync(
        Guid id,
        Guid orderId,
        long amountVnd,
        Guid? createdBy,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Inserting COD awaiting_settlement payment requires an active transaction.");

        db.Set<Payment>().Add(new Payment
        {
            Id = id,
            OrderId = orderId,
            Method = PaymentMethod.Cod,
            Provider = PaymentProvider.Manual,
            AmountVnd = amountVnd,
            Status = PaymentRecordStatus.AwaitingSettlement,
            BankReference = null,
            ReceivedAt = null,
            CodSettlementId = null,
            Note = null,
            VoidReason = null,
            CreatedBy = createdBy,
            CreatedAt = createdAt.UtcDateTime,
            ConfirmedBy = null,
            ConfirmedAt = null
        });

        await db.SaveChangesAsync(ct);
    }

    private async Task<ShipmentEventEntry[]> LoadOrderedEventsAsync(Guid shipmentId, CancellationToken ct)
    {
        var rows = await db.Set<PersistenceShipmentEvent>().AsNoTracking()
            .Where(x => x.ShipmentId == shipmentId)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        return rows.Select(e => new ShipmentEventEntry(
            e.Id,
            e.ShipmentId,
            e.Status,
            e.Note,
            e.Source,
            ToOffset(e.OccurredAt),
            e.CreatedBy)).ToArray();
    }

    private static DomainShipment Rehydrate(PersistenceShipment row, IReadOnlyList<ShipmentEventEntry> events) =>
        DomainShipment.Rehydrate(
            row.Id,
            row.OrderId,
            row.Purpose,
            row.ReturnRequestId,
            row.Provider,
            row.CarrierName,
            row.TrackingCode,
            row.Status,
            row.CodAmountVnd,
            row.ShippingCostVnd,
            row.ReturnCostVnd,
            row.Note,
            row.CreatedBy,
            ToOffset(row.CreatedAt),
            ToOffset(row.PickedUpAt),
            ToOffset(row.DeliveredAt),
            ToOffset(row.FailedAt),
            ToOffset(row.ReturnedAt),
            events);

    private static PersistenceShipment ToPersistenceShipment(DomainShipment shipment) => new()
    {
        Id = shipment.Id,
        OrderId = shipment.OrderId,
        Purpose = shipment.Purpose,
        ReturnRequestId = shipment.ReturnRequestId,
        Provider = shipment.Provider,
        CarrierName = shipment.CarrierName,
        TrackingCode = shipment.TrackingCode,
        Status = shipment.Status,
        CodAmountVnd = shipment.CodAmountVnd,
        ShippingCostVnd = shipment.ShippingCostVnd,
        ReturnCostVnd = shipment.ReturnCostVnd,
        Note = shipment.Note,
        CreatedBy = shipment.CreatedBy,
        CreatedAt = shipment.CreatedAt.UtcDateTime,
        PickedUpAt = shipment.PickedUpAt?.UtcDateTime,
        DeliveredAt = shipment.DeliveredAt?.UtcDateTime,
        FailedAt = shipment.FailedAt?.UtcDateTime,
        ReturnedAt = shipment.ReturnedAt?.UtcDateTime
    };

    private static PersistenceShipmentEvent ToPersistenceEvent(ShipmentEventEntry entry) => new()
    {
        Id = entry.Id,
        ShipmentId = entry.ShipmentId,
        Status = entry.Status,
        Note = entry.Note,
        Source = entry.Source,
        OccurredAt = entry.OccurredAt.UtcDateTime,
        CreatedBy = entry.CreatedBy
    };

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
}

public sealed class EfOrderShipmentQueries(AppDbContext db) : IOrderShipmentQueries
{
    public async Task<IReadOnlyList<ShipmentDetailView>> ListByOrderIdAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Set<PersistenceOrder>().AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.Code, o.Status, o.Version })
            .SingleOrDefaultAsync(ct);

        if (order is null) return [];

        var shipments = await db.Set<PersistenceShipment>().AsNoTracking()
            .Where(s => s.OrderId == orderId)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToArrayAsync(ct);

        if (shipments.Length == 0) return [];

        var shipmentIds = shipments.Select(s => s.Id).ToArray();
        var eventsByShipment = (await db.Set<PersistenceShipmentEvent>().AsNoTracking()
            .Where(e => shipmentIds.Contains(e.ShipmentId))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToArrayAsync(ct))
            .ToLookup(e => e.ShipmentId);

        var rowVersion = CatalogVersions.Encode(order.Version);

        return shipments.Select(s => new ShipmentDetailView(
            s.Id,
            s.OrderId,
            order.Code,
            order.Status,
            rowVersion,
            s.Purpose,
            s.ReturnRequestId,
            s.Provider,
            s.CarrierName,
            s.TrackingCode,
            s.Status,
            s.CodAmountVnd,
            s.ShippingCostVnd,
            s.ReturnCostVnd,
            s.Note,
            s.CreatedBy,
            ToOffset(s.CreatedAt),
            ToOffset(s.PickedUpAt),
            ToOffset(s.DeliveredAt),
            ToOffset(s.FailedAt),
            ToOffset(s.ReturnedAt),
            eventsByShipment[s.Id]
                .Select(e => new ShipmentEventView(
                    e.Id,
                    e.Status,
                    e.Note,
                    e.Source,
                    ToOffset(e.OccurredAt),
                    e.CreatedBy))
                .ToArray())).ToArray();
    }

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
}
