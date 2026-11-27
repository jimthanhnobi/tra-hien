using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm.Contracts;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Payments;
using TeaPremium.Application.Settings;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;
using PersistenceOrder = TeaPremium.Infrastructure.Persistence.Order;
using PersistenceShipment = TeaPremium.Infrastructure.Persistence.Shipment;

namespace TeaPremium.Infrastructure.Orders;

public sealed class EfAdminOrderQueries(
    AppDbContext db,
    IClock clock,
    IOrderPaymentLedgerReader paymentLedger,
    IOrderShipmentQueries shipmentQueries,
    ICustomerOrderResolver customerResolver,
    ISettingsStore settingsStore) : IAdminOrderQueries
{
    public async Task<CatalogPage<AdminOrderListItemView>> ListAsync(AdminOrderListFilter filter, CancellationToken ct)
    {
        IQueryable<PersistenceOrder> baseOrders = db.Set<PersistenceOrder>().AsNoTracking();

        if (filter.Q is { Length: > 0 } rawQ)
        {
            var normalized = await db.Database
                .SqlQuery<string>($"SELECT public.f_unaccent({rawQ}) AS \"Value\"")
                .SingleAsync(ct);

            var unaccentPattern = "%" + EscapeLike(normalized) + "%";
            var codePattern = "%" + EscapeLike(rawQ) + "%";
            var phoneTailPattern = filter.PhoneSearchDigits is { Length: > 0 } digits
                ? "%" + EscapeLike(digits)
                : null;

            if (phoneTailPattern is not null)
            {
                baseOrders = baseOrders.Where(o =>
                    EF.Functions.ILike(o.Code, codePattern, "\\")
                    || EF.Functions.ILike(EF.Functions.Unaccent(o.ContactName), unaccentPattern, "\\")
                    || EF.Functions.ILike(EF.Functions.Unaccent(o.ReceiverName), unaccentPattern, "\\")
                    || EF.Functions.Like(o.ContactPhone, phoneTailPattern, "\\")
                    || EF.Functions.Like(o.ReceiverPhone, phoneTailPattern, "\\"));
            }
            else
            {
                baseOrders = baseOrders.Where(o =>
                    EF.Functions.ILike(o.Code, codePattern, "\\")
                    || EF.Functions.ILike(EF.Functions.Unaccent(o.ContactName), unaccentPattern, "\\")
                    || EF.Functions.ILike(EF.Functions.Unaccent(o.ReceiverName), unaccentPattern, "\\"));
            }
        }

        if (filter.Status is not null)
            baseOrders = baseOrders.Where(o => o.Status == filter.Status);
        if (filter.PaymentStatus is not null)
            baseOrders = baseOrders.Where(o => o.PaymentStatus == filter.PaymentStatus);
        if (filter.Channel is not null)
            baseOrders = baseOrders.Where(o => o.SalesChannel == filter.Channel);
        if (filter.Origin is not null)
            baseOrders = baseOrders.Where(o => o.Origin == filter.Origin);
        if (filter.CustomerId is { } customerId)
            baseOrders = baseOrders.Where(o => o.CustomerId == customerId);
        if (filter.FromUtc is { } fromUtc)
            baseOrders = baseOrders.Where(o => o.PlacedAt >= fromUtc);
        if (filter.ToUtc is { } toUtc)
            baseOrders = filter.ToIsExclusive
                ? baseOrders.Where(o => o.PlacedAt < toUtc)
                : baseOrders.Where(o => o.PlacedAt <= toUtc);

        var nowUtc = clock.UtcNow.UtcDateTime;
        var expiringCutoffUtc = nowUtc.Add(NotificationContracts.OrderPaymentExpiringWindow);

        var projected = baseOrders.Select(o => new
        {
            Order = o,
            ConfirmedPaidVnd = db.Set<Payment>()
                .Where(p => p.OrderId == o.Id && p.Status == PaymentRecordStatus.Confirmed)
                .Sum(p => (long?)p.AmountVnd) ?? 0L,
            RefundedVnd = db.Set<Refund>()
                .Where(r => r.OrderId == o.Id)
                .Sum(r => (long?)r.AmountVnd) ?? 0L,
            OverpaymentRefundedVnd = db.Set<Refund>()
                .Where(r => r.OrderId == o.Id && r.Kind == RefundKind.Overpayment)
                .Sum(r => (long?)r.AmountVnd) ?? 0L,
            HasApprovedRefundReturn = db.Set<ReturnRequest>()
                .Any(rr => rr.OrderId == o.Id
                    && rr.Status == ReturnRequestStatus.Approved
                    && rr.Resolution == ReturnResolution.Refund),
            ItemCount = db.Set<OrderLine>()
                .Where(l => l.OrderId == o.Id)
                .Sum(l => (int?)l.Quantity) ?? 0
        });

        // SQL flag expressions aligned 100% with Owner-approved O1=A, O2=A, O3a=C, O3b=Phương án 3, and P2-13 (24h expiring window).
        if (filter.Flag == AdminOrderFlags.Overpaid)
        {
            projected = projected.Where(x =>
                !x.Order.IsBackfilled
                && x.Order.Status != OrderStatus.Cancelled
                && x.Order.Status != OrderStatus.DeliveryFailed
                && (x.ConfirmedPaidVnd - x.OverpaymentRefundedVnd) > x.Order.TotalVnd
                && (x.ConfirmedPaidVnd - x.RefundedVnd) > 0L);
        }
        else if (filter.Flag == AdminOrderFlags.NeedsRefund)
        {
            projected = projected.Where(x =>
                !x.Order.IsBackfilled
                && (((x.Order.Status == OrderStatus.Cancelled || x.Order.Status == OrderStatus.DeliveryFailed)
                        && (x.ConfirmedPaidVnd - x.RefundedVnd) > 0L)
                    || (x.Order.Status != OrderStatus.Cancelled
                        && x.Order.Status != OrderStatus.DeliveryFailed
                        && (x.ConfirmedPaidVnd - x.OverpaymentRefundedVnd) > x.Order.TotalVnd
                        && (x.ConfirmedPaidVnd - x.RefundedVnd) > 0L)
                    || x.HasApprovedRefundReturn));
        }
        else if (filter.Flag == AdminOrderFlags.Expiring)
        {
            // Aligned 100% with BR-ORD-05, ix_orders_due, and P2-13 EfDailyDigestRead: unpaid & total_vnd > 0 only.
            projected = projected.Where(x =>
                !x.Order.IsBackfilled
                && x.Order.Status == OrderStatus.Pending
                && x.Order.PaymentMethod == OrderPaymentMethod.BankTransfer
                && x.Order.TotalVnd > 0L
                && x.Order.PaymentStatus == OrderPaymentStatus.Unpaid
                && x.Order.PaymentDueAt != null
                && x.Order.PaymentDueAt > nowUtc
                && x.Order.PaymentDueAt <= expiringCutoffUtc);
        }

        var totalItems = await projected.LongCountAsync(ct);

        var ordered = filter.Sort switch
        {
            AdminOrderSortOptions.PlacedAtAsc => projected.OrderBy(x => x.Order.PlacedAt).ThenBy(x => x.Order.Id),
            AdminOrderSortOptions.TotalVndDesc => projected.OrderByDescending(x => x.Order.TotalVnd).ThenByDescending(x => x.Order.PlacedAt).ThenByDescending(x => x.Order.Id),
            AdminOrderSortOptions.TotalVndAsc => projected.OrderBy(x => x.Order.TotalVnd).ThenBy(x => x.Order.PlacedAt).ThenBy(x => x.Order.Id),
            AdminOrderSortOptions.DeliveredAtDesc => projected.OrderByDescending(x => x.Order.DeliveredAt != null).ThenByDescending(x => x.Order.DeliveredAt).ThenByDescending(x => x.Order.Id),
            AdminOrderSortOptions.DeliveredAtAsc => projected.OrderByDescending(x => x.Order.DeliveredAt != null).ThenBy(x => x.Order.DeliveredAt).ThenBy(x => x.Order.Id),
            _ => projected.OrderByDescending(x => x.Order.PlacedAt).ThenByDescending(x => x.Order.Id)
        };

        var rows = await ordered
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToArrayAsync(ct);

        var customerIds = rows.Select(x => x.Order.CustomerId).Distinct().ToArray();
        var customerCodes = await customerResolver.ResolveCustomerCodesAsync(customerIds, ct);

        var items = rows.Select(row =>
        {
            var o = row.Order;
            var paymentCalc = OrderPaymentStatusCalculator.Calculate(
                row.ConfirmedPaidVnd,
                row.RefundedVnd,
                row.OverpaymentRefundedVnd,
                o.TotalVnd,
                o.Status,
                o.IsBackfilled);
            var needsRefund = !o.IsBackfilled && (paymentCalc.NeedsRefund || row.HasApprovedRefundReturn);
            var isExpiring = IsExpiring(o, nowUtc, expiringCutoffUtc);

            return new AdminOrderListItemView(
                o.Id,
                o.Code,
                o.CustomerId,
                customerCodes.GetValueOrDefault(o.CustomerId),
                o.Origin,
                o.SalesChannel,
                o.Status,
                o.PaymentMethod,
                o.PaymentStatus,
                ToOffset(o.PaymentDueAt),
                o.ContactName,
                o.ContactPhone,
                o.ReceiverName,
                o.ReceiverPhone,
                o.SubtotalVnd,
                o.DiscountVnd,
                o.ShippingFeeVnd,
                o.TotalVnd,
                paymentCalc.ConfirmedPaidVnd,
                paymentCalc.RefundedVnd,
                paymentCalc.NetPaidVnd,
                paymentCalc.IsOverpaid,
                needsRefund,
                isExpiring,
                o.IsBackfilled,
                row.ItemCount,
                ToOffset(o.PlacedAt),
                ToOffset(o.ConfirmedAt),
                ToOffset(o.ShippedAt),
                ToOffset(o.DeliveredAt),
                ToOffset(o.FailedAt),
                ToOffset(o.CancelledAt),
                CatalogVersions.Encode(o.Version));
        }).ToArray();

        return new CatalogPage<AdminOrderListItemView>(items, filter.Page, filter.PageSize, totalItems);
    }

    public async Task<AdminOrderDetailView?> GetDetailAsync(
        Guid orderId,
        bool includeCosts,
        IReadOnlyList<AdminOrderWarningView>? warnings,
        CancellationToken ct)
    {
        var order = await db.Set<PersistenceOrder>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == orderId, ct);
        if (order is null) return null;

        var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(orderId, ct);
        var paymentCalc = OrderPaymentStatusCalculator.Calculate(
            ledger.ConfirmedPaidVnd,
            ledger.RefundedVnd,
            ledger.OverpaymentRefundedVnd,
            order.TotalVnd,
            order.Status,
            order.IsBackfilled);

        var lineRows = await db.Set<OrderLine>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Id)
            .ToArrayAsync(ct);

        var lines = lineRows.Select(l => new AdminOrderLineView(
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
            includeCosts ? l.UnitCostVnd : null,
            l.ReturnedQuantity)).ToArray();

        var paymentRows = await db.Set<Payment>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        var payments = paymentRows.Select(p => new AdminOrderPaymentView(
            p.Id,
            p.Method,
            p.Provider,
            p.AmountVnd,
            p.Status,
            p.BankReference,
            ToOffset(p.ReceivedAt),
            p.CodSettlementId,
            p.Note,
            p.VoidReason,
            p.CreatedBy,
            ToOffset(p.CreatedAt),
            p.ConfirmedBy,
            ToOffset(p.ConfirmedAt))).ToArray();

        var refundRows = await db.Set<Refund>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.RefundedAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        var refunds = refundRows.Select(r => new AdminOrderRefundView(
            r.Id,
            r.ReturnRequestId,
            r.Kind,
            r.AmountVnd,
            r.ShippingAmountVnd,
            r.Method,
            r.BankReference,
            r.Reason,
            ToOffset(r.RefundedAt),
            r.CreatedBy,
            ToOffset(r.CreatedAt))).ToArray();

        var shipments = await shipmentQueries.ListByOrderIdAsync(orderId, ct);

        var returnRows = await db.Set<ReturnRequest>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.RequestedAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        var returnIds = returnRows.Select(r => r.Id).ToArray();
        var returnLinesByRequest = returnIds.Length == 0
            ? Enumerable.Empty<ReturnLine>().ToLookup(x => x.ReturnRequestId)
            : (await db.Set<ReturnLine>().AsNoTracking()
                .Where(x => returnIds.Contains(x.ReturnRequestId))
                .OrderBy(x => x.Id)
                .ToArrayAsync(ct))
                .ToLookup(x => x.ReturnRequestId);

        var returns = returnRows.Select(rr => new AdminOrderReturnView(
            rr.Id,
            rr.Code,
            rr.Reason,
            rr.Description,
            rr.Status,
            rr.Resolution,
            rr.DecisionNote,
            ToOffset(rr.RequestedAt),
            rr.DecidedBy,
            ToOffset(rr.DecidedAt),
            ToOffset(rr.ResolvedAt),
            rr.CreatedBy,
            returnLinesByRequest[rr.Id]
                .Select(rl => new AdminOrderReturnLineView(rl.Id, rl.OrderLineId, rl.Quantity, rl.Restockable))
                .ToArray())).ToArray();

        var historyRows = await db.Set<OrderStatusHistory>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        var history = historyRows.Select(h => new AdminOrderHistoryView(
            h.Id,
            h.FromStatus,
            h.ToStatus,
            h.ActorType,
            h.ActorId,
            h.Reason,
            ToOffset(h.OccurredAt))).ToArray();

        var customerCodes = await customerResolver.ResolveCustomerCodesAsync([order.CustomerId], ct);
        var hasApprovedRefundReturn = returnRows.Any(rr =>
            rr.Status == ReturnRequestStatus.Approved && rr.Resolution == ReturnResolution.Refund);

        var nowUtc = clock.UtcNow.UtcDateTime;
        var expiringCutoffUtc = nowUtc.Add(NotificationContracts.OrderPaymentExpiringWindow);
        var needsRefund = !order.IsBackfilled && (paymentCalc.NeedsRefund || hasApprovedRefundReturn);
        var isExpiring = IsExpiring(order, nowUtc, expiringCutoffUtc);

        return new AdminOrderDetailView(
            order.Id,
            order.Code,
            order.CustomerId,
            customerCodes.GetValueOrDefault(order.CustomerId),
            order.Origin,
            order.SalesChannel,
            order.Status,
            order.PaymentMethod,
            order.PaymentStatus,
            ToOffset(order.PaymentDueAt),
            order.ContactName,
            order.ContactPhone,
            order.ContactEmail,
            order.ReceiverName,
            order.ReceiverPhone,
            order.ProvinceCode,
            order.ProvinceName,
            order.WardCode,
            order.WardName,
            order.AddressLine,
            order.SubtotalVnd,
            order.DiscountVnd,
            order.ShippingFeeVnd,
            order.TotalVnd,
            order.DiscountNote,
            order.VoucherId,
            order.ReferralCode,
            order.AcquisitionSource,
            order.IsFirstPurchase,
            order.CustomerNote,
            order.InternalNote,
            order.StatusReason,
            paymentCalc.ConfirmedPaidVnd,
            paymentCalc.RefundedVnd,
            paymentCalc.OverpaymentRefundedVnd,
            paymentCalc.EffectivePaidVnd,
            paymentCalc.NetPaidVnd,
            paymentCalc.RemainingToPayVnd,
            paymentCalc.OverpaidVnd,
            paymentCalc.IsOverpaid,
            needsRefund,
            isExpiring,
            order.IsBackfilled,
            order.CreatedByStaffId,
            ToOffset(order.PlacedAt),
            ToOffset(order.ConfirmedAt),
            ToOffset(order.ShippedAt),
            ToOffset(order.DeliveredAt),
            ToOffset(order.FailedAt),
            ToOffset(order.CancelledAt),
            ToOffset(order.CreatedAt),
            ToOffset(order.UpdatedAt),
            CatalogVersions.Encode(order.Version),
            lines,
            payments,
            refunds,
            shipments,
            returns,
            history,
            warnings ?? []);
    }

    public async Task<PackingSlipDocument?> GetPackingSlipAsync(Guid orderId, CancellationToken ct)
    {
        // Intentionally projects ONLY packing-slip safe fields — never selects unit_cost_vnd, internal_note, discount_note, or price_override_reason.
        var order = await db.Set<PersistenceOrder>().AsNoTracking()
            .Where(x => x.Id == orderId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Status,
                x.PaymentMethod,
                x.IsBackfilled,
                x.PlacedAt,
                x.ReceiverName,
                x.ReceiverPhone,
                x.AddressLine,
                x.WardName,
                x.ProvinceName,
                x.CustomerNote,
                x.SubtotalVnd,
                x.DiscountVnd,
                x.ShippingFeeVnd,
                x.TotalVnd
            })
            .SingleOrDefaultAsync(ct);

        if (order is null) return null;

        var lines = await db.Set<OrderLine>().AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Id)
            .Select(l => new PackingSlipLine(
                l.Sku,
                l.ProductName,
                l.VariantName,
                l.Quantity,
                l.UnitPriceVnd,
                l.LineTotalVnd))
            .ToArrayAsync(ct);

        var activeOrderShipment = await db.Set<PersistenceShipment>().AsNoTracking()
            .Where(s => s.OrderId == orderId
                && s.Purpose == ShipmentPurpose.Order
                && s.Status != ShipmentStatus.Cancelled
                && s.Status != ShipmentStatus.Failed
                && s.Status != ShipmentStatus.Returned)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.CodAmountVnd })
            .FirstOrDefaultAsync(ct);

        long codAmountVnd;
        if (activeOrderShipment is not null)
        {
            codAmountVnd = activeOrderShipment.CodAmountVnd;
        }
        else if (order.PaymentMethod == OrderPaymentMethod.Cod)
        {
            var ledger = await paymentLedger.GetOrderLedgerTotalsAsync(orderId, ct);
            var paymentCalc = OrderPaymentStatusCalculator.Calculate(
                ledger.ConfirmedPaidVnd,
                ledger.RefundedVnd,
                ledger.OverpaymentRefundedVnd,
                order.TotalVnd,
                order.Status,
                order.IsBackfilled);
            codAmountVnd = paymentCalc.RemainingToPayVnd;
        }
        else
        {
            codAmountVnd = 0L;
        }

        var siteSettings = await settingsStore.ReadAsync("site", forUpdate: false, ct);
        var shopName = ReadOptionalJsonString(siteSettings, "site.name") ?? "Trà Hiên";
        var shopHotline = ReadOptionalJsonString(siteSettings, "site.hotline");
        var shopAddress = ReadOptionalJsonString(siteSettings, "site.address");

        return new PackingSlipDocument(
            shopName,
            shopHotline,
            shopAddress,
            order.Code,
            ToOffset(order.PlacedAt),
            order.ReceiverName,
            order.ReceiverPhone,
            order.AddressLine,
            order.WardName,
            order.ProvinceName,
            order.CustomerNote,
            order.SubtotalVnd,
            order.DiscountVnd,
            order.ShippingFeeVnd,
            order.TotalVnd,
            codAmountVnd,
            lines);
    }

    private static bool IsExpiring(PersistenceOrder order, DateTime nowUtc, DateTime expiringCutoffUtc) =>
        !order.IsBackfilled
        && order.Status == OrderStatus.Pending
        && order.PaymentMethod == OrderPaymentMethod.BankTransfer
        && order.TotalVnd > 0L
        && order.PaymentStatus == OrderPaymentStatus.Unpaid
        && order.PaymentDueAt is { } due
        && due > nowUtc
        && due <= expiringCutoffUtc;

    private static string? ReadOptionalJsonString(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? (string.IsNullOrWhiteSpace(doc.RootElement.GetString()) ? null : doc.RootElement.GetString()!.Trim())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
