using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Settings;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using DomainOrder = TeaPremium.Domain.Orders.Order;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using PersistenceShipment = TeaPremium.Infrastructure.Persistence.Shipment;

namespace TeaPremium.Infrastructure.Orders;

public sealed class RandomOrderCodeGenerator : IOrderCodeGenerator
{
    public string NextCode(DateOnly vietnamDate) => OrderCode.GenerateRandom(vietnamDate);
}

public sealed class EfOrderPricingRead(ISettingsStore settingsStore) : IOrderPricingRead
{
    public async Task<OrderSettingsSnapshot> GetOrderSettingsAsync(CancellationToken ct)
    {
        var shipping = await settingsStore.ReadAsync("shipping", forUpdate: false, ct);
        var orders = await settingsStore.ReadAsync("orders", forUpdate: false, ct);

        return new(
            Math.Max(0L, ReadInt64(shipping, "shipping.flat_fee_vnd")),
            ReadNullableInt64(shipping, "shipping.free_threshold_vnd") is { } freeThreshold ? Math.Max(0L, freeThreshold) : null,
            ReadClampedInt32(orders, "orders.bank_transfer_hold_hours", 1, 24 * 365),
            ReadClampedInt32(orders, "orders.max_quantity_per_line_web", 1, 1000),
            ReadBool(orders, "orders.cod_requires_call"),
            ReadClampedInt32(orders, "orders.max_staff_discount_percent", 0, 100),
            ReadBool(orders, "orders.auto_confirm_on_full_payment"));
    }

    private static string Raw(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) ? raw : SettingsCatalog.Defaults[key];

    private static long ReadInt64(IReadOnlyDictionary<string, string> values, string key)
    {
        using var doc = JsonDocument.Parse(Raw(values, key));
        return doc.RootElement.GetInt64();
    }

    private static long? ReadNullableInt64(IReadOnlyDictionary<string, string> values, string key)
    {
        using var doc = JsonDocument.Parse(Raw(values, key));
        return doc.RootElement.ValueKind == JsonValueKind.Null ? null : doc.RootElement.GetInt64();
    }

    private static int ReadClampedInt32(IReadOnlyDictionary<string, string> values, string key, int min, int max)
    {
        using var doc = JsonDocument.Parse(Raw(values, key));
        return (int)Math.Clamp(doc.RootElement.GetInt64(), min, max);
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key)
    {
        using var doc = JsonDocument.Parse(Raw(values, key));
        return doc.RootElement.GetBoolean();
    }
}

// BR-CAT-02 / BR-CAT-06: variants referenced by any order line cannot be deleted or have their SKU edited.
public sealed class OrderVariantUsage(AppDbContext db) : IVariantUsage
{
    public Task<bool> UsedAsync(Guid variantId, CancellationToken ct) =>
        db.Set<OrderLine>().AsNoTracking().AnyAsync(x => x.VariantId == variantId, ct);
}

// R-P201-06: sales.order_status_history is append-only; no update or delete path exists here.
public sealed class EfOrderRepository(AppDbContext db, IOrderCodeGenerator codeGenerator) : IOrderRepository
{
    private const int MaxCodeAttempts = 10;

    public async Task<OrderLoadResult> LoadForUpdateAsync(Guid orderId, uint? expectedVersion, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Loading an order for update requires an active transaction.");

        var row = (await db.Set<PersistenceOrder>().FromSqlInterpolated(
            $"SELECT *, xmin FROM sales.orders WHERE id = {orderId} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();

        if (row is null) return new(null, false);
        if (expectedVersion is { } expected && row.Version != expected)
            return new(null, true);

        var lineRows = await db.Set<OrderLine>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Id)
            .ToArrayAsync(ct);

        var lines = lineRows.Select(l => new OrderLineItem(
            l.Id,
            l.VariantId,
            l.ProductId,
            l.Sku,
            l.ProductName,
            l.VariantName,
            l.Quantity,
            l.ListPriceVnd,
            l.UnitPriceVnd,
            l.PriceSource,
            l.PriceOverrideReason,
            l.LineTotalVnd,
            l.UnitCostVnd,
            l.ReturnedQuantity,
            ToOffset(l.ReviewRequestedAt))).ToArray();

        var domain = DomainOrder.Rehydrate(
            row.Id,
            row.Code,
            row.Origin,
            row.SalesChannel,
            row.CustomerId,
            row.CustomerAccountId,
            row.Status,
            row.PaymentMethod,
            row.PaymentStatus,
            ToOffset(row.PaymentDueAt),
            new(
                row.ContactName,
                row.ContactPhone,
                row.ContactEmail,
                row.ReceiverName,
                row.ReceiverPhone,
                row.ProvinceCode,
                row.ProvinceName,
                row.WardCode,
                row.WardName,
                row.AddressLine),
            row.SubtotalVnd,
            row.DiscountVnd,
            row.ShippingFeeVnd,
            row.TotalVnd,
            row.DiscountNote,
            row.VoucherId,
            row.ReferralCode,
            new(row.AcquisitionSource, row.IsFirstPurchase, row.TrackingLinkId, row.CampaignId, row.Utm),
            row.CustomerNote,
            row.InternalNote,
            row.StatusReason,
            ToOffset(row.PlacedAt),
            ToOffset(row.ConfirmedAt),
            ToOffset(row.ShippedAt),
            ToOffset(row.DeliveredAt),
            ToOffset(row.FailedAt),
            ToOffset(row.CancelledAt),
            row.InvoiceNumber,
            ToOffset(row.InvoiceIssuedAt),
            row.IsBackfilled,
            row.CreatedByStaffId,
            ToOffset(row.CreatedAt),
            ToOffset(row.UpdatedAt),
            row.Version,
            lines);

        return new(domain, false);
    }

    // Matches ux_shipment_active_order predicate: purpose = 'order' AND status NOT IN ('cancelled','failed','returned').
    public Task<bool> HasActiveOrderShipmentAsync(Guid orderId, CancellationToken ct) =>
        db.Set<PersistenceShipment>().AsNoTracking()
            .AnyAsync(x => x.OrderId == orderId
                && x.Purpose == "order"
                && x.Status != "cancelled"
                && x.Status != "failed"
                && x.Status != "returned", ct);

    public Task<bool> HasOrderShipmentAsync(Guid orderId, CancellationToken ct) =>
        HasActiveOrderShipmentAsync(orderId, ct);

    public async Task<string?> InsertAsync(DomainOrder order, DateOnly vietnamDate, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Inserting an order requires an active transaction.");

        PersistenceOrder? persisted = null;
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var candidate = codeGenerator.NextCode(vietnamDate);
            var row = ToPersistenceOrder(order, candidate);
            var prior = db.ChangeTracker.Entries()
                .Select(x => x.Entity)
                .ToHashSet(ReferenceEqualityComparer.Instance);

            await tx.CreateSavepointAsync("order_code_candidate", ct);
            db.Set<PersistenceOrder>().Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.ReleaseSavepointAsync("order_code_candidate", ct);
                persisted = row;
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_orders_code" })
            {
                await tx.RollbackToSavepointAsync("order_code_candidate", ct);
                // Detach both the failed Order and any AuditLog emitted by AuditSaveChangesInterceptor.
                foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                    entry.State = EntityState.Detached;
                await tx.ReleaseSavepointAsync("order_code_candidate", ct);
            }
        }

        if (persisted is null) return null;

        order.AssignPersistedCode(persisted.Code);
        order.SetVersion(persisted.Version);

        foreach (var line in order.Lines)
            db.Set<OrderLine>().Add(ToPersistenceLine(order.Id, line));

        AppendHistory(order);
        await db.SaveChangesAsync(ct);
        return persisted.Code;
    }

    public async Task SaveUpdatedOrderAsync(DomainOrder order, OrderLineDelta? lineDelta, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Updating an order requires an active transaction.");

        var row = db.Set<PersistenceOrder>().Local.SingleOrDefault(x => x.Id == order.Id)
            ?? await db.Set<PersistenceOrder>().SingleAsync(x => x.Id == order.Id, ct);

        row.Status = order.Status;
        row.PaymentStatus = order.PaymentStatus;
        row.PaymentDueAt = order.PaymentDueAt?.UtcDateTime;
        row.SubtotalVnd = order.SubtotalVnd;
        row.DiscountVnd = order.DiscountVnd;
        row.ShippingFeeVnd = order.ShippingFeeVnd;
        row.TotalVnd = order.TotalVnd;
        row.DiscountNote = order.DiscountNote;
        row.VoucherId = order.VoucherId;
        row.StatusReason = order.StatusReason;
        row.ConfirmedAt = order.ConfirmedAt?.UtcDateTime;
        row.ShippedAt = order.ShippedAt?.UtcDateTime;
        row.DeliveredAt = order.DeliveredAt?.UtcDateTime;
        row.FailedAt = order.FailedAt?.UtcDateTime;
        row.CancelledAt = order.CancelledAt?.UtcDateTime;
        row.UpdatedAt = order.UpdatedAt.UtcDateTime;

        if (lineDelta is not null)
        {
            var trackedLines = await db.Set<OrderLine>()
                .Where(x => x.OrderId == order.Id)
                .ToDictionaryAsync(x => x.Id, ct);

            foreach (var removed in lineDelta.Removed)
            {
                if (trackedLines.TryGetValue(removed.Id, out var existing))
                    db.Set<OrderLine>().Remove(existing);
            }

            foreach (var (_, after) in lineDelta.QuantityIncreased.Concat(lineDelta.QuantityDecreased).Concat(lineDelta.UnchangedQuantity))
            {
                if (trackedLines.TryGetValue(after.Id, out var existing))
                    ApplyLineUpdates(existing, after);
            }

            foreach (var added in lineDelta.Added)
                db.Set<OrderLine>().Add(ToPersistenceLine(order.Id, added));
        }
        else if (order.Lines.Any(l => l.UnitCostVnd is not null))
        {
            var trackedLines = await db.Set<OrderLine>()
                .Where(x => x.OrderId == order.Id)
                .ToDictionaryAsync(x => x.Id, ct);

            foreach (var line in order.Lines)
            {
                if (trackedLines.TryGetValue(line.Id, out var existing) && existing.UnitCostVnd != line.UnitCostVnd)
                    existing.UnitCostVnd = line.UnitCostVnd;
            }
        }

        AppendHistory(order);
        await db.SaveChangesAsync(ct);
        order.SetVersion(row.Version);
    }

    private void AppendHistory(DomainOrder order)
    {
        foreach (var entry in order.DrainNewHistory())
        {
            db.Set<OrderStatusHistory>().Add(new OrderStatusHistory
            {
                Id = entry.Id,
                OrderId = entry.OrderId,
                FromStatus = entry.FromStatus,
                ToStatus = entry.ToStatus,
                ActorType = entry.ActorType,
                ActorId = entry.ActorId,
                Reason = entry.Reason,
                OccurredAt = entry.OccurredAt.UtcDateTime
            });
        }
    }

    private static PersistenceOrder ToPersistenceOrder(DomainOrder order, string code) => new()
    {
        Id = order.Id,
        Code = code,
        Origin = order.Origin,
        SalesChannel = order.SalesChannel,
        CustomerId = order.CustomerId,
        CustomerAccountId = order.CustomerAccountId,
        Status = order.Status,
        PaymentMethod = order.PaymentMethod,
        PaymentStatus = order.PaymentStatus,
        PaymentDueAt = order.PaymentDueAt?.UtcDateTime,
        ContactName = order.Contact.ContactName,
        ContactPhone = order.Contact.ContactPhone,
        ContactEmail = order.Contact.ContactEmail,
        ReceiverName = order.Contact.ReceiverName,
        ReceiverPhone = order.Contact.ReceiverPhone,
        ProvinceCode = order.Contact.ProvinceCode,
        ProvinceName = order.Contact.ProvinceName,
        WardCode = order.Contact.WardCode,
        WardName = order.Contact.WardName,
        AddressLine = order.Contact.AddressLine,
        SubtotalVnd = order.SubtotalVnd,
        DiscountVnd = order.DiscountVnd,
        ShippingFeeVnd = order.ShippingFeeVnd,
        TotalVnd = order.TotalVnd,
        DiscountNote = order.DiscountNote,
        VoucherId = order.VoucherId,
        ReferralCode = order.ReferralCode,
        AcquisitionSource = order.Attribution.AcquisitionSource,
        IsFirstPurchase = order.Attribution.IsFirstPurchase,
        TrackingLinkId = order.Attribution.TrackingLinkId,
        CampaignId = order.Attribution.CampaignId,
        Utm = order.Attribution.Utm,
        CustomerNote = order.CustomerNote,
        InternalNote = order.InternalNote,
        StatusReason = order.StatusReason,
        PlacedAt = order.PlacedAt.UtcDateTime,
        ConfirmedAt = order.ConfirmedAt?.UtcDateTime,
        ShippedAt = order.ShippedAt?.UtcDateTime,
        DeliveredAt = order.DeliveredAt?.UtcDateTime,
        FailedAt = order.FailedAt?.UtcDateTime,
        CancelledAt = order.CancelledAt?.UtcDateTime,
        InvoiceNumber = order.InvoiceNumber,
        InvoiceIssuedAt = order.InvoiceIssuedAt?.UtcDateTime,
        IsBackfilled = order.IsBackfilled,
        CreatedByStaffId = order.CreatedByStaffId,
        CreatedAt = order.CreatedAt.UtcDateTime,
        UpdatedAt = order.UpdatedAt.UtcDateTime
    };

    private static OrderLine ToPersistenceLine(Guid orderId, OrderLineItem line) => new()
    {
        Id = line.Id,
        OrderId = orderId,
        VariantId = line.VariantId,
        ProductId = line.ProductId,
        Sku = line.Sku,
        ProductName = line.ProductName,
        VariantName = line.VariantName,
        Quantity = line.Quantity,
        ListPriceVnd = line.ListPriceVnd,
        UnitPriceVnd = line.UnitPriceVnd,
        PriceSource = line.PriceSource,
        PriceOverrideReason = line.PriceOverrideReason,
        LineTotalVnd = line.LineTotalVnd,
        UnitCostVnd = line.UnitCostVnd,
        ReturnedQuantity = line.ReturnedQuantity,
        ReviewRequestedAt = line.ReviewRequestedAt?.UtcDateTime
    };

    private static void ApplyLineUpdates(OrderLine existing, OrderLineItem line)
    {
        existing.Sku = line.Sku;
        existing.ProductName = line.ProductName;
        existing.VariantName = line.VariantName;
        existing.Quantity = line.Quantity;
        existing.ListPriceVnd = line.ListPriceVnd;
        existing.UnitPriceVnd = line.UnitPriceVnd;
        existing.PriceSource = line.PriceSource;
        existing.PriceOverrideReason = line.PriceOverrideReason;
        existing.LineTotalVnd = line.LineTotalVnd;
        existing.UnitCostVnd = line.UnitCostVnd;
    }

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
}
