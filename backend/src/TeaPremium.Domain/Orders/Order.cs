using System.Text.RegularExpressions;

namespace TeaPremium.Domain.Orders;

public sealed record OrderContactSnapshot(
    string ContactName,
    string ContactPhone,
    string? ContactEmail,
    string ReceiverName,
    string ReceiverPhone,
    string ProvinceCode,
    string ProvinceName,
    string? WardCode,
    string WardName,
    string AddressLine);

public sealed record OrderAttributionSnapshot(
    string AcquisitionSource,
    bool IsFirstPurchase,
    Guid? TrackingLinkId = null,
    Guid? CampaignId = null,
    string? Utm = null);

public sealed record OrderLineItem(
    Guid Id,
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? VariantName,
    int Quantity,
    long ListPriceVnd,
    long UnitPriceVnd,
    string PriceSource,
    string? PriceOverrideReason,
    long LineTotalVnd,
    long? UnitCostVnd = null,
    int ReturnedQuantity = 0,
    DateTimeOffset? ReviewRequestedAt = null)
{
    public static OrderLineItem Create(
        Guid id,
        Guid variantId,
        Guid productId,
        string sku,
        string productName,
        string? variantName,
        int quantity,
        long listPriceVnd,
        long unitPriceVnd,
        string priceSource,
        string? priceOverrideReason,
        long? unitCostVnd = null,
        int returnedQuantity = 0,
        DateTimeOffset? reviewRequestedAt = null)
    {
        if (id == Guid.Empty || variantId == Guid.Empty || productId == Guid.Empty)
            throw new DomainException("validation_failed", "Định danh dòng đơn hàng không hợp lệ.");
        if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(productName))
            throw new DomainException("validation_failed", "SKU và tên sản phẩm dòng đơn hàng là bắt buộc.");
        if (quantity is < 1 or > 1000)
            throw new DomainException("validation_failed", "Số lượng dòng đơn hàng phải nằm trong khoảng 1 đến 1000.");
        if (listPriceVnd < 0 || unitPriceVnd < 0)
            throw new DomainException("validation_failed", "Đơn giá dòng đơn hàng không được âm.");
        if (!OrderPriceSource.IsValid(priceSource))
            throw new DomainException("validation_failed", "Nguồn giá dòng đơn hàng không hợp lệ.");
        var trimmedOverride = string.IsNullOrWhiteSpace(priceOverrideReason) ? null : priceOverrideReason.Trim();
        if (priceSource == OrderPriceSource.Override && (trimmedOverride is null || trimmedOverride.Length < 3))
            throw new DomainException("validation_failed", "Lý do sửa giá dòng phải có ít nhất 3 ký tự.");
        if (unitCostVnd is < 0)
            throw new DomainException("validation_failed", "Giá vốn dòng đơn hàng không được âm.");
        if (returnedQuantity < 0 || returnedQuantity > quantity)
            throw new DomainException("validation_failed", "Số lượng trả lại không hợp lệ.");

        var lineTotal = checked((long)quantity * unitPriceVnd);
        return new(
            id,
            variantId,
            productId,
            sku.Trim(),
            productName.Trim(),
            string.IsNullOrWhiteSpace(variantName) ? null : variantName.Trim(),
            quantity,
            listPriceVnd,
            unitPriceVnd,
            priceSource,
            priceSource == OrderPriceSource.Override ? trimmedOverride : null,
            lineTotal,
            unitCostVnd,
            returnedQuantity,
            reviewRequestedAt);
    }

    public OrderLineItem WithUnitCost(long? unitCostVnd)
    {
        if (unitCostVnd is < 0)
            throw new DomainException("validation_failed", "Giá vốn dòng đơn hàng không được âm.");
        return this with { UnitCostVnd = unitCostVnd };
    }
}

public sealed record OrderStatusHistoryEntry(
    Guid Id,
    Guid OrderId,
    string? FromStatus,
    string ToStatus,
    string ActorType,
    Guid? ActorId,
    string? Reason,
    DateTimeOffset OccurredAt);

public sealed record OrderDomainEvent(
    string EventType,
    Guid OrderId,
    string OrderCode,
    string Status,
    DateTimeOffset OccurredAt,
    string? ReasonCode = null);

public sealed record OrderLineDelta(
    IReadOnlyList<OrderLineItem> Added,
    IReadOnlyList<OrderLineItem> Removed,
    IReadOnlyList<(OrderLineItem Before, OrderLineItem After)> QuantityIncreased,
    IReadOnlyList<(OrderLineItem Before, OrderLineItem After)> QuantityDecreased,
    IReadOnlyList<(OrderLineItem Before, OrderLineItem After)> UnchangedQuantity);

public sealed partial class Order
{
    private readonly List<OrderLineItem> _lines;
    private readonly List<OrderStatusHistoryEntry> _newHistory = [];
    private readonly List<OrderDomainEvent> _events = [];

    private Order(
        Guid id,
        string code,
        string origin,
        string salesChannel,
        Guid customerId,
        Guid? customerAccountId,
        string status,
        string paymentMethod,
        string paymentStatus,
        DateTimeOffset? paymentDueAt,
        OrderContactSnapshot contact,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        string? discountNote,
        Guid? voucherId,
        string? referralCode,
        OrderAttributionSnapshot attribution,
        string? customerNote,
        string? internalNote,
        string? statusReason,
        DateTimeOffset placedAt,
        DateTimeOffset? confirmedAt,
        DateTimeOffset? shippedAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? failedAt,
        DateTimeOffset? cancelledAt,
        string? invoiceNumber,
        DateTimeOffset? invoiceIssuedAt,
        bool isBackfilled,
        Guid? createdByStaffId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        uint version,
        List<OrderLineItem> lines)
    {
        Id = id;
        Code = code;
        Origin = origin;
        SalesChannel = salesChannel;
        CustomerId = customerId;
        CustomerAccountId = customerAccountId;
        Status = status;
        PaymentMethod = paymentMethod;
        PaymentStatus = paymentStatus;
        PaymentDueAt = paymentDueAt;
        Contact = contact;
        SubtotalVnd = subtotalVnd;
        DiscountVnd = discountVnd;
        ShippingFeeVnd = shippingFeeVnd;
        TotalVnd = totalVnd;
        DiscountNote = discountNote;
        VoucherId = voucherId;
        ReferralCode = referralCode;
        Attribution = attribution;
        CustomerNote = customerNote;
        InternalNote = internalNote;
        StatusReason = statusReason;
        PlacedAt = placedAt;
        ConfirmedAt = confirmedAt;
        ShippedAt = shippedAt;
        DeliveredAt = deliveredAt;
        FailedAt = failedAt;
        CancelledAt = cancelledAt;
        InvoiceNumber = invoiceNumber;
        InvoiceIssuedAt = invoiceIssuedAt;
        IsBackfilled = isBackfilled;
        CreatedByStaffId = createdByStaffId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
        _lines = lines;
    }

    public Guid Id { get; }
    public string Code { get; private set; }
    public string Origin { get; }
    public string SalesChannel { get; }
    public Guid CustomerId { get; }
    public Guid? CustomerAccountId { get; }
    public string Status { get; private set; }
    public string PaymentMethod { get; }
    public string PaymentStatus { get; private set; }
    public DateTimeOffset? PaymentDueAt { get; private set; }
    public OrderContactSnapshot Contact { get; private set; }
    public long SubtotalVnd { get; private set; }
    public long DiscountVnd { get; private set; }
    public long ShippingFeeVnd { get; private set; }
    public long TotalVnd { get; private set; }
    public string? DiscountNote { get; private set; }
    public Guid? VoucherId { get; private set; }
    public string? ReferralCode { get; }
    public OrderAttributionSnapshot Attribution { get; }
    public string? CustomerNote { get; private set; }
    public string? InternalNote { get; private set; }
    public string? StatusReason { get; private set; }
    public DateTimeOffset PlacedAt { get; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? InvoiceNumber { get; }
    public DateTimeOffset? InvoiceIssuedAt { get; }
    public bool IsBackfilled { get; }
    public Guid? CreatedByStaffId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyList<OrderLineItem> Lines => _lines;

    public static Order PlaceWeb(
        Guid id,
        string code,
        Guid customerId,
        Guid? customerAccountId,
        string paymentMethod,
        DateTimeOffset? paymentDueAt,
        OrderContactSnapshot contact,
        IReadOnlyList<OrderLineItem> lines,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        Guid? voucherId,
        string? discountNote,
        OrderAttributionSnapshot attribution,
        string? customerNote,
        DateTimeOffset placedAt)
    {
        var initialPayment = OrderPaymentStatusCalculator.Calculate(
            confirmedPaidVnd: 0L,
            refundedVnd: 0L,
            overpaymentRefundedVnd: 0L,
            totalVnd: totalVnd,
            orderStatus: OrderStatus.Pending,
            isBackfilled: false);

        ValidateCommon(id, code, customerId, paymentMethod, initialPayment.Status, contact, attribution,
            lines, subtotalVnd, discountVnd, shippingFeeVnd, totalVnd, allowOverrideLines: false);

        if (paymentMethod == OrderPaymentMethod.BankTransfer)
        {
            if (paymentDueAt is null || paymentDueAt <= placedAt)
                throw new DomainException("validation_failed", "Đơn chuyển khoản web bắt buộc có hạn thanh toán hợp lệ.");
        }
        else if (paymentDueAt is not null)
        {
            throw new DomainException("validation_failed", "Đơn COD không có hạn thanh toán.");
        }

        var order = new Order(
            id,
            code,
            OrderOrigin.Web,
            Orders.SalesChannel.Web,
            customerId,
            customerAccountId,
            OrderStatus.Pending,
            paymentMethod,
            initialPayment.Status,
            paymentDueAt,
            NormalizeContact(contact),
            subtotalVnd,
            discountVnd,
            shippingFeeVnd,
            totalVnd,
            NormalizeOptional(discountNote),
            voucherId,
            null,
            attribution,
            NormalizeOptional(customerNote),
            null,
            null,
            placedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            placedAt,
            placedAt,
            0,
            [.. lines]);

        order.AppendHistory(null, OrderStatus.Pending, OrderActorType.Customer, customerAccountId, null, placedAt);
        order.AppendEvent(OrderEventTypes.OrderPlaced, OrderStatus.Pending, placedAt);
        return order;
    }

    public static Order CreateAdmin(
        Guid id,
        string code,
        string salesChannel,
        Guid customerId,
        Guid createdByStaffId,
        string paymentMethod,
        OrderContactSnapshot contact,
        IReadOnlyList<OrderLineItem> lines,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        Guid? voucherId,
        string? discountNote,
        OrderAttributionSnapshot attribution,
        string? customerNote,
        string? internalNote,
        DateTimeOffset placedAt,
        bool confirmNow = false,
        bool codRequiresCall = true,
        bool codCallConfirmed = false,
        string? confirmNote = null)
    {
        // R-P201-04 / BR-ORD-04: admin orders must never use sales_channel = 'web'.
        if (!Orders.SalesChannel.IsValidForAdmin(salesChannel))
            throw new DomainException("invalid_sales_channel", "Đơn hàng tạo từ quản trị phải chọn kênh bán khác web.");
        if (createdByStaffId == Guid.Empty)
            throw new DomainException("invalid_actor", "Đơn hàng quản trị bắt buộc có mã nhân viên tạo.");

        var initialStatus = confirmNow ? OrderStatus.Confirmed : OrderStatus.Pending;
        var initialPayment = OrderPaymentStatusCalculator.Calculate(
            confirmedPaidVnd: 0L,
            refundedVnd: 0L,
            overpaymentRefundedVnd: 0L,
            totalVnd: totalVnd,
            orderStatus: initialStatus,
            isBackfilled: false);

        ValidateCommon(id, code, customerId, paymentMethod, initialPayment.Status, contact, attribution,
            lines, subtotalVnd, discountVnd, shippingFeeVnd, totalVnd, allowOverrideLines: true);

        var trimmedConfirmNote = NormalizeOptional(confirmNote);
        if (confirmNow && paymentMethod == OrderPaymentMethod.Cod && codRequiresCall && (!codCallConfirmed || trimmedConfirmNote is null))
            throw new DomainException("cod_confirmation_required", "Đơn COD cần xác nhận đã gọi khách và ghi chú.");

        var confirmedAt = confirmNow ? placedAt : (DateTimeOffset?)null;

        var order = new Order(
            id,
            code,
            OrderOrigin.Admin,
            salesChannel,
            customerId,
            null,
            initialStatus,
            paymentMethod,
            initialPayment.Status,
            null,
            NormalizeContact(contact),
            subtotalVnd,
            discountVnd,
            shippingFeeVnd,
            totalVnd,
            NormalizeOptional(discountNote),
            voucherId,
            null,
            attribution,
            NormalizeOptional(customerNote),
            NormalizeOptional(internalNote),
            null,
            placedAt,
            confirmedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            createdByStaffId,
            placedAt,
            placedAt,
            0,
            [.. lines]);

        order.AppendHistory(null, initialStatus, OrderActorType.Staff, createdByStaffId, confirmNow ? trimmedConfirmNote : null, placedAt);
        order.AppendEvent(OrderEventTypes.OrderPlaced, OrderStatus.Pending, placedAt);
        if (confirmNow)
            order.AppendEvent(OrderEventTypes.OrderConfirmed, OrderStatus.Confirmed, placedAt);
        return order;
    }

    public static Order Rehydrate(
        Guid id,
        string code,
        string origin,
        string salesChannel,
        Guid customerId,
        Guid? customerAccountId,
        string status,
        string paymentMethod,
        string paymentStatus,
        DateTimeOffset? paymentDueAt,
        OrderContactSnapshot contact,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        string? discountNote,
        Guid? voucherId,
        string? referralCode,
        OrderAttributionSnapshot attribution,
        string? customerNote,
        string? internalNote,
        string? statusReason,
        DateTimeOffset placedAt,
        DateTimeOffset? confirmedAt,
        DateTimeOffset? shippedAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? failedAt,
        DateTimeOffset? cancelledAt,
        string? invoiceNumber,
        DateTimeOffset? invoiceIssuedAt,
        bool isBackfilled,
        Guid? createdByStaffId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        uint version,
        IReadOnlyList<OrderLineItem> lines)
    {
        if (!OrderOrigin.IsValid(origin) || !Orders.SalesChannel.IsValid(salesChannel) || !OrderStatus.IsValid(status)
            || !OrderPaymentMethod.IsValid(paymentMethod) || !OrderPaymentStatus.IsValid(paymentStatus))
            throw new DomainException("validation_failed", "Trạng thái hoặc thuộc tính đơn hàng không hợp lệ.");

        return new Order(
            id,
            code,
            origin,
            salesChannel,
            customerId,
            customerAccountId,
            status,
            paymentMethod,
            paymentStatus,
            paymentDueAt,
            contact,
            subtotalVnd,
            discountVnd,
            shippingFeeVnd,
            totalVnd,
            discountNote,
            voucherId,
            referralCode,
            attribution,
            customerNote,
            internalNote,
            statusReason,
            placedAt,
            confirmedAt,
            shippedAt,
            deliveredAt,
            failedAt,
            cancelledAt,
            invoiceNumber,
            invoiceIssuedAt,
            isBackfilled,
            createdByStaffId,
            createdAt,
            updatedAt,
            version,
            [.. lines]);
    }

    public void ApplyPaymentCalculation(OrderPaymentCalculation calculation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(calculation);
        if (!OrderPaymentStatus.IsValid(calculation.Status))
            throw new DomainException("validation_failed", "Trạng thái thanh toán không hợp lệ.");
        if (calculation.TotalVnd != TotalVnd)
            throw new DomainException("validation_failed", "Tổng tiền tính trạng thái thanh toán không khớp tổng đơn hàng.");

        if (!string.Equals(PaymentStatus, calculation.Status, StringComparison.Ordinal))
        {
            PaymentStatus = calculation.Status;
            UpdatedAt = at;
        }
    }

    public void Confirm(
        string actorType,
        Guid? actorId,
        DateTimeOffset at,
        bool codRequiresCall = true,
        bool codCallConfirmed = false,
        string? note = null,
        string? reasonCode = null)
    {
        if (Status != OrderStatus.Pending)
            throw new DomainException("invalid_status_transition", "Chỉ đơn ở trạng thái chờ mới có thể xác nhận.");
        ValidateStaffOrSystemActor(actorType, actorId);

        if (reasonCode is not null
            && (actorType != OrderActorType.System
                || !string.Equals(reasonCode, OrderConfirmReasonCodes.AutoConfirmedOnFullPayment, StringComparison.Ordinal)))
            throw new DomainException("validation_failed", "Mã lý do xác nhận đơn hàng không hợp lệ.");

        if (Origin == OrderOrigin.Web && PaymentMethod == OrderPaymentMethod.BankTransfer && PaymentStatus != OrderPaymentStatus.Paid)
            throw new DomainException("invalid_status_transition", "Đơn chuyển khoản web phải thanh toán đủ trước khi xác nhận.");

        var trimmedNote = NormalizeOptional(note);
        if (PaymentMethod == OrderPaymentMethod.Cod && actorType == OrderActorType.Staff && codRequiresCall
            && (!codCallConfirmed || trimmedNote is null))
            throw new DomainException("cod_confirmation_required", "Đơn COD cần xác nhận đã gọi khách và ghi chú.");

        var from = Status;
        Status = OrderStatus.Confirmed;
        ConfirmedAt = at;
        UpdatedAt = at;
        AppendHistory(from, Status, actorType, actorId, trimmedNote, at);
        AppendEvent(OrderEventTypes.OrderConfirmed, Status, at, reasonCode);
    }

    public void Cancel(
        string actorType,
        Guid? actorId,
        string? reason,
        DateTimeOffset at,
        bool hasActiveOrderShipment = false)
    {
        if (Status is not (OrderStatus.Pending or OrderStatus.Confirmed))
            throw new DomainException("invalid_status_transition", "Đơn hàng ở trạng thái hiện tại không thể hủy.");

        if (!OrderActorType.IsValid(actorType))
            throw new DomainException("invalid_actor", "Tác nhân hủy đơn không hợp lệ.");

        if (Status == OrderStatus.Confirmed)
        {
            if (actorType != OrderActorType.Staff)
                throw new DomainException("invalid_status_transition", "Chỉ nhân viên mới có thể hủy đơn đã xác nhận.");
            if (hasActiveOrderShipment)
                throw new DomainException("invalid_status_transition", "Không thể hủy đơn khi đang có vận đơn hoạt động.");
        }

        if (actorType == OrderActorType.Staff && (actorId is null || actorId == Guid.Empty))
            throw new DomainException("invalid_actor", "Thiếu mã nhân viên thực hiện hủy đơn.");
        if (actorType == OrderActorType.System && actorId is not null)
            throw new DomainException("invalid_actor", "Tác nhân hệ thống không có mã định danh cá nhân.");

        var trimmedReason = NormalizeOptional(reason);
        if (actorType is OrderActorType.Staff or OrderActorType.System && trimmedReason is null)
            throw new DomainException("cancel_reason_required", "Lý do hủy đơn là bắt buộc.");

        var from = Status;
        Status = OrderStatus.Cancelled;
        CancelledAt = at;
        StatusReason = trimmedReason;
        UpdatedAt = at;
        AppendHistory(from, Status, actorType, actorId, trimmedReason, at);
        var safeReasonCode = actorType == OrderActorType.System && string.Equals(trimmedReason, "payment_expired", StringComparison.Ordinal)
            ? "payment_expired"
            : null;
        AppendEvent(OrderEventTypes.OrderCancelled, Status, at, safeReasonCode);
    }

    public void MarkShipping(
        Guid staffId,
        DateTimeOffset at,
        bool hasOrderShipment,
        string? note = null)
    {
        if (Status != OrderStatus.Confirmed)
            throw new DomainException("invalid_status_transition", "Chỉ đơn đã xác nhận mới có thể chuyển sang đang giao.");
        if (staffId == Guid.Empty)
            throw new DomainException("invalid_actor", "Thiếu mã nhân viên giao hàng.");
        if (!hasOrderShipment)
            throw new DomainException("invalid_status_transition", "Đơn hàng phải có vận đơn hợp lệ trước khi chuyển đang giao.");
        if (PaymentMethod != OrderPaymentMethod.Cod && PaymentStatus != OrderPaymentStatus.Paid)
            throw new DomainException("invalid_status_transition", "Đơn chuyển khoản phải thanh toán đủ trước khi xuất kho giao hàng.");

        var from = Status;
        Status = OrderStatus.Shipping;
        ShippedAt = at;
        UpdatedAt = at;
        AppendHistory(from, Status, OrderActorType.Staff, staffId, NormalizeOptional(note), at);
        AppendEvent(OrderEventTypes.OrderShipped, Status, at);
    }

    public void MarkDelivered(
        string actorType,
        Guid? actorId,
        DateTimeOffset deliveredAt,
        DateTimeOffset now,
        string? note = null)
    {
        if (Status != OrderStatus.Shipping)
            throw new DomainException("invalid_status_transition", "Chỉ đơn đang giao mới có thể đánh dấu đã giao.");
        ValidateStaffOrSystemActor(actorType, actorId);
        if (deliveredAt > now || (ShippedAt is { } shipped && deliveredAt < shipped))
            throw new DomainException("invalid_delivered_at", "Thời điểm giao thành công không hợp lệ.");

        var from = Status;
        Status = OrderStatus.Delivered;
        DeliveredAt = deliveredAt;
        UpdatedAt = now;
        AppendHistory(from, Status, actorType, actorId, NormalizeOptional(note), now);
        AppendEvent(OrderEventTypes.OrderDelivered, Status, now);
    }

    public void MarkDeliveryFailed(
        string actorType,
        Guid? actorId,
        string? reason,
        DateTimeOffset failedAt,
        DateTimeOffset? now = null)
    {
        if (Status != OrderStatus.Shipping)
            throw new DomainException("invalid_status_transition", "Chỉ đơn đang giao mới có thể đánh dấu giao thất bại.");
        ValidateStaffOrSystemActor(actorType, actorId);
        var trimmedReason = NormalizeOptional(reason);
        if (trimmedReason is null)
            throw new DomainException("failure_reason_required", "Lý do giao thất bại là bắt buộc.");

        var effectiveNow = now ?? failedAt;
        if (failedAt > effectiveNow || (ShippedAt is { } shipped && failedAt < shipped))
            throw new DomainException("invalid_event_time", "Thời điểm giao thất bại không hợp lệ.");

        var from = Status;
        Status = OrderStatus.DeliveryFailed;
        FailedAt = failedAt;
        StatusReason = trimmedReason;
        UpdatedAt = effectiveNow;
        AppendHistory(from, Status, actorType, actorId, trimmedReason, effectiveNow);
        AppendEvent(OrderEventTypes.OrderDeliveryFailed, Status, effectiveNow);
    }

    public OrderLineDelta ReplaceLines(
        IReadOnlyList<OrderLineItem> nextLines,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        Guid? voucherId,
        string? discountNote,
        DateTimeOffset at)
    {
        if (!OrderStatus.AllowsLineMutation(Status))
            throw new DomainException("invalid_status_transition", "Chỉ được sửa hoặc xóa dòng hàng khi đơn đang chờ hoặc đã xác nhận.");

        ValidateLinesAndTotals(nextLines, subtotalVnd, discountVnd, shippingFeeVnd, totalVnd, allowOverrideLines: true);

        var existingByVariant = _lines.ToDictionary(x => x.VariantId);
        var nextByVariant = nextLines.ToDictionary(x => x.VariantId);

        var added = new List<OrderLineItem>();
        var removed = new List<OrderLineItem>();
        var increased = new List<(OrderLineItem Before, OrderLineItem After)>();
        var decreased = new List<(OrderLineItem Before, OrderLineItem After)>();
        var unchangedQty = new List<(OrderLineItem Before, OrderLineItem After)>();
        var normalizedNext = new List<OrderLineItem>(nextLines.Count);

        foreach (var existing in _lines)
        {
            if (!nextByVariant.ContainsKey(existing.VariantId))
                removed.Add(existing);
        }

        foreach (var next in nextLines)
        {
            if (!existingByVariant.TryGetValue(next.VariantId, out var existing))
            {
                added.Add(next);
                normalizedNext.Add(next);
                continue;
            }

            // Preserve existing order_line_id when updating an existing variant line.
            var preserved = next with { Id = existing.Id, UnitCostVnd = existing.UnitCostVnd, ReturnedQuantity = existing.ReturnedQuantity, ReviewRequestedAt = existing.ReviewRequestedAt };
            normalizedNext.Add(preserved);
            if (preserved.Quantity > existing.Quantity)
                increased.Add((existing, preserved));
            else if (preserved.Quantity < existing.Quantity)
                decreased.Add((existing, preserved));
            else
                unchangedQty.Add((existing, preserved));
        }

        _lines.Clear();
        _lines.AddRange(normalizedNext);
        SubtotalVnd = subtotalVnd;
        DiscountVnd = discountVnd;
        ShippingFeeVnd = shippingFeeVnd;
        TotalVnd = totalVnd;
        VoucherId = voucherId;
        DiscountNote = NormalizeOptional(discountNote);
        UpdatedAt = at;

        return new(added, removed, increased, decreased, unchangedQty);
    }

    public void LockLineUnitCosts(IReadOnlyDictionary<Guid, long?> unitCostByLineId)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (unitCostByLineId.TryGetValue(line.Id, out var cost))
                _lines[i] = line.WithUnitCost(cost);
        }
    }

    public void AssignPersistedCode(string code)
    {
        if (!OrderCode.IsValid(code))
            throw new DomainException("invalid_order_code", "Mã đơn hàng không đúng định dạng.");
        Code = code;
        for (var i = 0; i < _events.Count; i++)
            _events[i] = _events[i] with { OrderCode = code };
    }

    public void SetVersion(uint version) => Version = version;

    public IReadOnlyList<OrderStatusHistoryEntry> DrainNewHistory()
    {
        var items = _newHistory.ToArray();
        _newHistory.Clear();
        return items;
    }

    public IReadOnlyList<OrderDomainEvent> DrainEvents()
    {
        var items = _events.ToArray();
        _events.Clear();
        return items;
    }

    private void AppendHistory(string? fromStatus, string toStatus, string actorType, Guid? actorId, string? reason, DateTimeOffset occurredAt) =>
        _newHistory.Add(new(Guid.CreateVersion7(), Id, fromStatus, toStatus, actorType, actorId, reason, occurredAt));

    private void AppendEvent(string eventType, string status, DateTimeOffset occurredAt, string? reasonCode = null) =>
        _events.Add(new(eventType, Id, Code, status, occurredAt, reasonCode));

    private static void ValidateCommon(
        Guid id,
        string code,
        Guid customerId,
        string paymentMethod,
        string paymentStatus,
        OrderContactSnapshot contact,
        OrderAttributionSnapshot attribution,
        IReadOnlyList<OrderLineItem> lines,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        bool allowOverrideLines)
    {
        if (id == Guid.Empty)
            throw new DomainException("validation_failed", "Mã định danh đơn hàng không hợp lệ.");
        if (!OrderCode.IsValid(code))
            throw new DomainException("invalid_order_code", "Mã đơn hàng không đúng định dạng.");
        if (customerId == Guid.Empty)
            throw new DomainException("validation_failed", "Đơn hàng bắt buộc gắn với khách hàng.");
        if (!OrderPaymentMethod.IsValid(paymentMethod))
            throw new DomainException("validation_failed", "Phương thức thanh toán không hợp lệ.");
        if (!OrderPaymentStatus.IsValid(paymentStatus))
            throw new DomainException("validation_failed", "Trạng thái thanh toán không hợp lệ.");
        if (!OrderAcquisitionSource.IsValid(attribution.AcquisitionSource))
            throw new DomainException("validation_failed", "Nguồn khách hàng không hợp lệ.");

        ValidateContact(contact);
        ValidateLinesAndTotals(lines, subtotalVnd, discountVnd, shippingFeeVnd, totalVnd, allowOverrideLines);
    }

    private static void ValidateContact(OrderContactSnapshot contact)
    {
        if (string.IsNullOrWhiteSpace(contact.ContactName)
            || string.IsNullOrWhiteSpace(contact.ReceiverName)
            || string.IsNullOrWhiteSpace(contact.ProvinceCode)
            || string.IsNullOrWhiteSpace(contact.ProvinceName)
            || string.IsNullOrWhiteSpace(contact.WardName)
            || string.IsNullOrWhiteSpace(contact.AddressLine))
            throw new DomainException("validation_failed", "Thông tin liên hệ và giao hàng chưa đầy đủ.");

        if (!VietnamPhonePattern().IsMatch(contact.ContactPhone) || !VietnamPhonePattern().IsMatch(contact.ReceiverPhone))
            throw new DomainException("invalid_phone", "Số điện thoại phải ở định dạng +84xxxxxxxxx.");
    }

    private static OrderContactSnapshot NormalizeContact(OrderContactSnapshot contact) => new(
        contact.ContactName.Trim(),
        contact.ContactPhone.Trim(),
        NormalizeOptional(contact.ContactEmail),
        contact.ReceiverName.Trim(),
        contact.ReceiverPhone.Trim(),
        contact.ProvinceCode.Trim(),
        contact.ProvinceName.Trim(),
        NormalizeOptional(contact.WardCode),
        contact.WardName.Trim(),
        contact.AddressLine.Trim());

    private static void ValidateLinesAndTotals(
        IReadOnlyList<OrderLineItem> lines,
        long subtotalVnd,
        long discountVnd,
        long shippingFeeVnd,
        long totalVnd,
        bool allowOverrideLines)
    {
        if (lines.Count == 0)
            throw new DomainException("validation_failed", "Đơn hàng phải có ít nhất một dòng sản phẩm.");

        var seenVariants = new HashSet<Guid>();
        long computedSubtotal = 0;
        foreach (var line in lines)
        {
            if (!seenVariants.Add(line.VariantId))
                throw new DomainException("validation_failed", "Đơn hàng không được chứa hai dòng trùng biến thể.");
            if (!allowOverrideLines && line.PriceSource == OrderPriceSource.Override)
                throw new DomainException("validation_failed", "Đơn web không được sửa giá tay.");
            if (line.Quantity is < 1 or > 1000 || line.ListPriceVnd < 0 || line.UnitPriceVnd < 0
                || line.LineTotalVnd != checked((long)line.Quantity * line.UnitPriceVnd))
                throw new DomainException("validation_failed", "Dòng sản phẩm đơn hàng không hợp lệ.");
            if (line.PriceSource == OrderPriceSource.Override && (line.PriceOverrideReason is null || line.PriceOverrideReason.Trim().Length < 3))
                throw new DomainException("validation_failed", "Lý do sửa giá dòng phải có ít nhất 3 ký tự.");

            computedSubtotal = checked(computedSubtotal + line.LineTotalVnd);
        }

        if (subtotalVnd < 0 || discountVnd < 0 || shippingFeeVnd < 0 || totalVnd < 0)
            throw new DomainException("validation_failed", "Số tiền đơn hàng không được âm.");
        if (subtotalVnd != computedSubtotal)
            throw new DomainException("validation_failed", "Tổng tiền hàng không khớp tổng các dòng.");
        if (discountVnd > subtotalVnd)
            throw new DomainException("validation_failed", "Giảm giá không được vượt quá tiền hàng.");
        if (totalVnd != checked(subtotalVnd - discountVnd + shippingFeeVnd))
            throw new DomainException("validation_failed", "Tổng thanh toán không khớp công thức đơn hàng.");
    }

    private static void ValidateStaffOrSystemActor(string actorType, Guid? actorId)
    {
        if (actorType == OrderActorType.Staff)
        {
            if (actorId is null || actorId == Guid.Empty)
                throw new DomainException("invalid_actor", "Thiếu mã nhân viên thực hiện thao tác.");
            return;
        }
        if (actorType == OrderActorType.System)
        {
            if (actorId is not null)
                throw new DomainException("invalid_actor", "Tác nhân hệ thống không có mã định danh cá nhân.");
            return;
        }
        throw new DomainException("invalid_actor", "Tác nhân không có quyền thực hiện thao tác này.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^\\+84[0-9]{9}$", RegexOptions.CultureInvariant)]
    private static partial Regex VietnamPhonePattern();
}
