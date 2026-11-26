using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Payments;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;

namespace TeaPremium.Infrastructure.Payments;

public sealed class EfOrderPaymentLedgerReader(AppDbContext db) : IOrderPaymentLedgerReader
{
    public async Task<OrderPaymentLedgerTotals> GetOrderLedgerTotalsAsync(Guid orderId, CancellationToken ct)
    {
        var confirmedPaidVnd = await db.Set<Payment>().AsNoTracking()
            .Where(x => x.OrderId == orderId && x.Status == PaymentRecordStatus.Confirmed)
            .SumAsync(x => (long?)x.AmountVnd, ct) ?? 0L;

        var refundGroups = await db.Set<Refund>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .GroupBy(x => x.Kind)
            .Select(g => new
            {
                Kind = g.Key,
                TotalAmountVnd = g.Sum(x => x.AmountVnd),
                TotalShippingVnd = g.Sum(x => x.ShippingAmountVnd)
            })
            .ToArrayAsync(ct);

        long refundedVnd = 0;
        long overpaymentRefundedVnd = 0;
        long returnGoodsRefundedVnd = 0;
        long adjustmentGoodsRefundedVnd = 0;
        long shippingRefundedVnd = 0;

        foreach (var row in refundGroups)
        {
            refundedVnd = checked(refundedVnd + row.TotalAmountVnd);
            shippingRefundedVnd = checked(shippingRefundedVnd + row.TotalShippingVnd);
            var goodsVnd = checked(row.TotalAmountVnd - row.TotalShippingVnd);

            if (row.Kind == RefundKind.Overpayment)
                overpaymentRefundedVnd = checked(overpaymentRefundedVnd + row.TotalAmountVnd);
            else if (row.Kind == RefundKind.Return)
                returnGoodsRefundedVnd = checked(returnGoodsRefundedVnd + goodsVnd);
            else if (row.Kind == RefundKind.Adjustment)
                adjustmentGoodsRefundedVnd = checked(adjustmentGoodsRefundedVnd + goodsVnd);
        }

        return new OrderPaymentLedgerTotals(
            confirmedPaidVnd,
            refundedVnd,
            overpaymentRefundedVnd,
            returnGoodsRefundedVnd,
            adjustmentGoodsRefundedVnd,
            shippingRefundedVnd);
    }
}

public sealed class EfPaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<bool> ExistsActiveBankReferenceAsync(string provider, string bankReference, CancellationToken ct) =>
        db.Set<Payment>().AsNoTracking()
            .AnyAsync(x => x.Provider == provider
                && x.BankReference == bankReference
                && x.Status != PaymentRecordStatus.Voided, ct);

    public async Task<bool> TryInsertWithSavepointAsync(NewPaymentRecord payment, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Recording a payment requires an active transaction.");

        var row = new Payment
        {
            Id = payment.Id,
            OrderId = payment.OrderId,
            Method = payment.Method,
            Provider = payment.Provider,
            AmountVnd = payment.AmountVnd,
            Status = payment.Status,
            BankReference = payment.BankReference,
            ReceivedAt = payment.ReceivedAt.UtcDateTime,
            CodSettlementId = null,
            Note = payment.Note,
            VoidReason = null,
            CreatedBy = payment.CreatedBy,
            CreatedAt = payment.CreatedAt.UtcDateTime,
            ConfirmedBy = payment.ConfirmedBy,
            ConfirmedAt = payment.ConfirmedAt.UtcDateTime
        };

        var prior = db.ChangeTracker.Entries()
            .Select(x => x.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        await tx.CreateSavepointAsync("payment_bank_ref", ct);
        db.Set<Payment>().Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync("payment_bank_ref", ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_payments_bank_ref" })
        {
            await tx.RollbackToSavepointAsync("payment_bank_ref", ct);
            foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
            await tx.ReleaseSavepointAsync("payment_bank_ref", ct);
            return false;
        }
    }

    public Task<PaymentLookupForVoid?> FindOrderForVoidAsync(Guid paymentId, CancellationToken ct) =>
        db.Set<Payment>().AsNoTracking()
            .Where(x => x.Id == paymentId)
            .Select(x => new PaymentLookupForVoid(x.Id, x.OrderId))
            .SingleOrDefaultAsync(ct);

    public async Task<LockedPaymentRecord?> LoadForUpdateAsync(Guid paymentId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Loading a payment for update requires an active transaction.");

        var row = (await db.Set<Payment>().FromSqlInterpolated(
            $"SELECT * FROM sales.payments WHERE id = {paymentId} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();

        if (row is null) return null;

        return new LockedPaymentRecord(
            row.Id,
            row.OrderId,
            row.Method,
            row.Provider,
            row.AmountVnd,
            row.Status,
            row.CodSettlementId,
            ToOffset(row.ConfirmedAt));
    }

    public async Task VoidAsync(Guid paymentId, string voidReason, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Voiding a payment requires an active transaction.");

        var row = db.Set<Payment>().Local.SingleOrDefault(x => x.Id == paymentId)
            ?? await db.Set<Payment>().SingleAsync(x => x.Id == paymentId, ct);

        // R-P201-01: update status, confirmed_at=NULL and void_reason simultaneously to satisfy ck_payments_confirmed.
        row.Status = PaymentRecordStatus.Voided;
        row.ConfirmedAt = null;
        row.VoidReason = voidReason;

        await db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
}

// R-P201-06: strictly append-only repository for sales.refunds (no update or delete methods).
public sealed class EfRefundRepository(AppDbContext db) : IRefundRepository
{
    public async Task<bool> TryInsertWithSavepointAsync(NewRefundRecord refund, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Recording a refund requires an active transaction.");

        var row = new Refund
        {
            Id = refund.Id,
            OrderId = refund.OrderId,
            ReturnRequestId = refund.ReturnRequestId,
            Kind = refund.Kind,
            AmountVnd = refund.AmountVnd,
            ShippingAmountVnd = refund.ShippingAmountVnd,
            Method = refund.Method,
            BankReference = refund.BankReference,
            Reason = refund.Reason,
            RefundedAt = refund.RefundedAt.UtcDateTime,
            CreatedBy = refund.CreatedBy,
            CreatedAt = refund.CreatedAt.UtcDateTime
        };

        var prior = db.ChangeTracker.Entries()
            .Select(x => x.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        await tx.CreateSavepointAsync("refund_return_request", ct);
        db.Set<Refund>().Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync("refund_return_request", ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_refunds_return_request" })
        {
            await tx.RollbackToSavepointAsync("refund_return_request", ct);
            foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
            await tx.ReleaseSavepointAsync("refund_return_request", ct);
            return false;
        }
    }

    public async Task<long> SumRefundedForOrderAsync(Guid orderId, CancellationToken ct) =>
        await db.Set<Refund>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .SumAsync(x => (long?)x.AmountVnd, ct) ?? 0L;
}

public sealed class EfReturnRefundStore(AppDbContext db) : IReturnRefundPort
{
    public async Task<LockedReturnRequestForRefund?> LoadForUpdateAsync(Guid orderId, Guid returnRequestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Loading a return request for refund requires an active transaction.");

        // Check order ownership before locking to avoid cross-order deadlock when mismatched IDs are submitted.
        var peek = await db.Set<ReturnRequest>().AsNoTracking()
            .Where(x => x.Id == returnRequestId)
            .Select(x => new { x.Id, x.Code, x.OrderId, x.Status, x.Resolution })
            .SingleOrDefaultAsync(ct);

        if (peek is null) return null;
        if (peek.OrderId != orderId)
            return new LockedReturnRequestForRefund(peek.Id, peek.Code, peek.OrderId, peek.Status, peek.Resolution, []);

        var row = (await db.Set<ReturnRequest>().FromSqlInterpolated(
            $"SELECT *, xmin FROM sales.return_requests WHERE id = {returnRequestId} AND order_id = {orderId} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();

        if (row is null) return null;

        var lines = await db.Set<ReturnLine>().AsNoTracking()
            .Where(x => x.ReturnRequestId == row.Id)
            .OrderBy(x => x.OrderLineId)
            .Select(x => new ReturnLineRefundInput(x.OrderLineId, x.Quantity))
            .ToArrayAsync(ct);

        return new LockedReturnRequestForRefund(row.Id, row.Code, row.OrderId, row.Status, row.Resolution, lines);
    }

    // Counts only lines belonging to prior resolution='refund' requests that already have a kind='return' refund row.
    // Never reads order_lines.returned_quantity and never includes replacement requests.
    public async Task<IReadOnlyDictionary<Guid, int>> GetPriorRefundedQuantitiesByOrderLineAsync(
        Guid orderId,
        Guid excludeReturnRequestId,
        CancellationToken ct)
    {
        var rows = await (
            from rf in db.Set<Refund>().AsNoTracking()
            join rr in db.Set<ReturnRequest>().AsNoTracking() on rf.ReturnRequestId equals rr.Id
            join rl in db.Set<ReturnLine>().AsNoTracking() on rr.Id equals rl.ReturnRequestId
            where rf.OrderId == orderId
                && rf.Kind == RefundKind.Return
                && rf.ReturnRequestId != null
                && rr.OrderId == orderId
                && rr.Resolution == ReturnResolution.Refund
                && rr.Id != excludeReturnRequestId
            group rl by rl.OrderLineId into g
            select new
            {
                OrderLineId = g.Key,
                Quantity = g.Sum(x => x.Quantity)
            }).ToArrayAsync(ct);

        return rows.ToDictionary(x => x.OrderLineId, x => x.Quantity);
    }

    public Task<bool> HasOtherApprovedRefundReturnAsync(
        Guid orderId,
        Guid? excludeReturnRequestId,
        CancellationToken ct) =>
        db.Set<ReturnRequest>().AsNoTracking()
            .AnyAsync(rr => rr.OrderId == orderId
                && rr.Status == ReturnRequestStatus.Approved
                && rr.Resolution == ReturnResolution.Refund
                && (excludeReturnRequestId == null || rr.Id != excludeReturnRequestId.Value), ct);

    public async Task MarkResolvedAsync(Guid returnRequestId, DateTimeOffset resolvedAt, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Resolving a return request requires an active transaction.");

        var row = db.Set<ReturnRequest>().Local.SingleOrDefault(x => x.Id == returnRequestId)
            ?? await db.Set<ReturnRequest>().SingleAsync(x => x.Id == returnRequestId, ct);

        row.Status = ReturnRequestStatus.Resolved;
        row.ResolvedAt = resolvedAt.UtcDateTime;

        await db.SaveChangesAsync(ct);
    }
}

public sealed class EfPaymentQueries(AppDbContext db) : IPaymentQueries
{
    public async Task<AwaitingPaymentsPage> GetAwaitingAsync(
        string type,
        string? carrier,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var skip = (page - 1) * pageSize;

        if (type == AwaitingPaymentType.BankTransfer)
        {
            // Owner-approved 2026-11-26 (ASSUMPTION-P207-02=A & O3a=C): filter by effective_paid < total_vnd
            // on active bank_transfer non-backfilled orders (automatically excludes 0-VND orders and fully settled orders).
            var baseQuery = db.Set<PersistenceOrder>().AsNoTracking()
                .Where(o => o.PaymentMethod == OrderPaymentMethod.BankTransfer
                    && !o.IsBackfilled
                    && (o.Status == OrderStatus.Pending
                        || o.Status == OrderStatus.Confirmed
                        || o.Status == OrderStatus.Shipping))
                .Select(o => new
                {
                    o.Id,
                    o.Code,
                    o.Origin,
                    o.SalesChannel,
                    o.Status,
                    o.PaymentStatus,
                    o.TotalVnd,
                    o.PlacedAt,
                    o.PaymentDueAt,
                    ConfirmedPaidVnd = db.Set<Payment>()
                        .Where(p => p.OrderId == o.Id && p.Status == PaymentRecordStatus.Confirmed)
                        .Sum(p => (long?)p.AmountVnd) ?? 0L,
                    OverpaymentRefundedVnd = db.Set<Refund>()
                        .Where(r => r.OrderId == o.Id && r.Kind == RefundKind.Overpayment)
                        .Sum(r => (long?)r.AmountVnd) ?? 0L
                })
                .Where(x => (x.ConfirmedPaidVnd - x.OverpaymentRefundedVnd) < x.TotalVnd);

            var totalItems = await baseQuery.LongCountAsync(ct);
            var totalAmountVnd = totalItems == 0
                ? 0L
                : await baseQuery.SumAsync(x => x.TotalVnd - (x.ConfirmedPaidVnd - x.OverpaymentRefundedVnd), ct);

            var rows = await baseQuery
                .OrderBy(x => x.PaymentDueAt == null ? 1 : 0)
                .ThenBy(x => x.PaymentDueAt)
                .ThenBy(x => x.PlacedAt)
                .ThenBy(x => x.Id)
                .Skip(skip)
                .Take(pageSize)
                .ToArrayAsync(ct);

            var items = rows.Select(x =>
            {
                var effectivePaid = checked(x.ConfirmedPaidVnd - x.OverpaymentRefundedVnd);
                var remaining = checked(x.TotalVnd - effectivePaid);
                return new AwaitingBankTransferItem(
                    x.Id,
                    x.Code,
                    x.Origin,
                    x.SalesChannel,
                    x.Status,
                    x.PaymentStatus,
                    x.TotalVnd,
                    x.ConfirmedPaidVnd,
                    effectivePaid,
                    remaining,
                    ToOffset(x.PlacedAt),
                    ToOffset(x.PaymentDueAt));
            }).ToArray();

            return new AwaitingPaymentsPage(type, page, pageSize, totalItems, totalAmountVnd, items, []);
        }

        // ASSUMPTION-P207-02=A & R-P207-06: pick single latest delivered purpose='order' shipment per order to prevent duplicate rows.
        var carrierPattern = string.IsNullOrWhiteSpace(carrier) ? null : EscapeLike(carrier.Trim());
        var codQuery = from p in db.Set<Payment>().AsNoTracking()
                       join o in db.Set<PersistenceOrder>().AsNoTracking() on p.OrderId equals o.Id
                       let deliveredShipment = db.Set<Shipment>().AsNoTracking()
                           .Where(s => s.OrderId == p.OrderId && s.Purpose == "order" && s.Status == "delivered")
                           .OrderByDescending(s => s.DeliveredAt)
                           .ThenByDescending(s => s.CreatedAt)
                           .ThenByDescending(s => s.Id)
                           .Select(s => new { s.CarrierName, s.TrackingCode, s.DeliveredAt })
                           .FirstOrDefault()
                       where p.Status == PaymentRecordStatus.AwaitingSettlement
                           && p.Method == PaymentMethod.Cod
                           && (carrierPattern == null
                               || (deliveredShipment != null && EF.Functions.ILike(deliveredShipment.CarrierName.Trim(), carrierPattern, "\\")))
                       select new
                       {
                           p.Id,
                           p.OrderId,
                           o.Code,
                           p.AmountVnd,
                           CarrierName = deliveredShipment != null ? deliveredShipment.CarrierName : null,
                           TrackingCode = deliveredShipment != null ? deliveredShipment.TrackingCode : null,
                           DeliveredAt = deliveredShipment != null ? deliveredShipment.DeliveredAt : null,
                           p.CreatedAt
                       };

        var codTotalItems = await codQuery.LongCountAsync(ct);
        var codTotalAmountVnd = codTotalItems == 0
            ? 0L
            : await codQuery.SumAsync(x => (long?)x.AmountVnd, ct) ?? 0L;

        var codRows = await codQuery
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToArrayAsync(ct);

        var codItems = codRows.Select(x => new AwaitingCodPaymentItem(
            x.Id,
            x.OrderId,
            x.Code,
            x.AmountVnd,
            x.CarrierName,
            x.TrackingCode,
            ToOffset(x.DeliveredAt),
            ToOffset(x.CreatedAt))).ToArray();

        return new AwaitingPaymentsPage(type, page, pageSize, codTotalItems, codTotalAmountVnd, [], codItems);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
}
