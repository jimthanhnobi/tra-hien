namespace TeaPremium.Domain.Orders;

public sealed record ShipmentEventEntry(
    Guid Id,
    Guid ShipmentId,
    string Status,
    string? Note,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? CreatedBy);

public sealed class Shipment
{
    public const int MaxCarrierNameLength = 100;
    public const int MaxTrackingCodeLength = 100;
    public const int MaxNoteLength = 500;

    private readonly List<ShipmentEventEntry> _events;
    private readonly List<ShipmentEventEntry> _newEvents = [];

    private Shipment(
        Guid id,
        Guid orderId,
        string purpose,
        Guid? returnRequestId,
        string provider,
        string carrierName,
        string? trackingCode,
        string status,
        long codAmountVnd,
        long? shippingCostVnd,
        long? returnCostVnd,
        string? note,
        Guid? createdBy,
        DateTimeOffset createdAt,
        DateTimeOffset? pickedUpAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? failedAt,
        DateTimeOffset? returnedAt,
        List<ShipmentEventEntry> events)
    {
        Id = id;
        OrderId = orderId;
        Purpose = purpose;
        ReturnRequestId = returnRequestId;
        Provider = provider;
        CarrierName = carrierName;
        TrackingCode = trackingCode;
        Status = status;
        CodAmountVnd = codAmountVnd;
        ShippingCostVnd = shippingCostVnd;
        ReturnCostVnd = returnCostVnd;
        Note = note;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        PickedUpAt = pickedUpAt;
        DeliveredAt = deliveredAt;
        FailedAt = failedAt;
        ReturnedAt = returnedAt;
        _events = events;
    }

    public Guid Id { get; }
    public Guid OrderId { get; }
    public string Purpose { get; }
    public Guid? ReturnRequestId { get; }
    public string Provider { get; }
    public string CarrierName { get; }
    public string? TrackingCode { get; private set; }
    public string Status { get; private set; }
    public long CodAmountVnd { get; }
    public long? ShippingCostVnd { get; private set; }
    public long? ReturnCostVnd { get; private set; }
    public string? Note { get; }
    public Guid? CreatedBy { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? PickedUpAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public DateTimeOffset? ReturnedAt { get; private set; }
    public IReadOnlyList<ShipmentEventEntry> Events => _events;
    public IReadOnlyList<ShipmentEventEntry> PendingNewEvents => _newEvents;

    // BR-SHP-02 (O3a = C, Owner-approved 2026-11-26): reuse P2-07's OrderPaymentStatusCalculator directly
    // so 0-VND orders, deposits, and overpayment refunds stay identical to BR-PAY-02 without duplicating formulas.
    public static long ComputeOrderCodAmountVnd(string orderPaymentMethod, OrderPaymentCalculation paymentCalculation)
    {
        ArgumentNullException.ThrowIfNull(paymentCalculation);
        if (!OrderPaymentMethod.IsValid(orderPaymentMethod))
            throw new DomainException("validation_failed", "Phương thức thanh toán đơn hàng không hợp lệ.");

        return orderPaymentMethod == OrderPaymentMethod.Cod
            ? paymentCalculation.RemainingToPayVnd
            : 0L;
    }

    // ASSUMPTION-P208-01 = A (Owner-approved 2026-11-26): insert initial 'created' shipment_events row upon creation.
    public static Shipment CreateForOrder(
        Guid id,
        Guid orderId,
        string provider,
        string carrierName,
        string? trackingCode,
        long codAmountVnd,
        long? shippingCostVnd,
        string? note,
        Guid? createdBy,
        DateTimeOffset createdAt)
    {
        ValidateCreateCommon(id, orderId, provider, carrierName, trackingCode, codAmountVnd, shippingCostVnd, note, createdBy, createdAt,
            out var normalizedCarrier, out var normalizedTracking, out var normalizedNote);

        var shipment = new Shipment(
            id,
            orderId,
            ShipmentPurpose.Order,
            null,
            provider,
            normalizedCarrier,
            normalizedTracking,
            ShipmentStatus.Created,
            codAmountVnd,
            shippingCostVnd,
            null,
            normalizedNote,
            createdBy,
            createdAt,
            null,
            null,
            null,
            null,
            []);

        shipment.AppendEvent(ShipmentStatus.Created, normalizedNote, ShipmentEventSource.Manual, createdAt, createdBy);
        return shipment;
    }

    // G30 seam for P2-10: replacement shipments require returnRequestId (ck_shipments_row) and always carry 0 COD.
    public static Shipment CreateForReplacement(
        Guid id,
        Guid orderId,
        Guid returnRequestId,
        string provider,
        string carrierName,
        string? trackingCode,
        long? shippingCostVnd,
        string? note,
        Guid? createdBy,
        DateTimeOffset createdAt)
    {
        if (returnRequestId == Guid.Empty)
            throw new DomainException("validation_failed", "Vận đơn gửi hàng thay thế bắt buộc gắn với yêu cầu đổi trả.");

        ValidateCreateCommon(id, orderId, provider, carrierName, trackingCode, 0L, shippingCostVnd, note, createdBy, createdAt,
            out var normalizedCarrier, out var normalizedTracking, out var normalizedNote);

        var shipment = new Shipment(
            id,
            orderId,
            ShipmentPurpose.Replacement,
            returnRequestId,
            provider,
            normalizedCarrier,
            normalizedTracking,
            ShipmentStatus.Created,
            0L,
            shippingCostVnd,
            null,
            normalizedNote,
            createdBy,
            createdAt,
            null,
            null,
            null,
            null,
            []);

        shipment.AppendEvent(ShipmentStatus.Created, normalizedNote, ShipmentEventSource.Manual, createdAt, createdBy);
        return shipment;
    }

    public static Shipment Rehydrate(
        Guid id,
        Guid orderId,
        string purpose,
        Guid? returnRequestId,
        string provider,
        string carrierName,
        string? trackingCode,
        string status,
        long codAmountVnd,
        long? shippingCostVnd,
        long? returnCostVnd,
        string? note,
        Guid? createdBy,
        DateTimeOffset createdAt,
        DateTimeOffset? pickedUpAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? failedAt,
        DateTimeOffset? returnedAt,
        IReadOnlyList<ShipmentEventEntry> events)
    {
        if (!ShipmentPurpose.IsValid(purpose) || !ShippingProviderCode.IsValid(provider) || !ShipmentStatus.IsValid(status))
            throw new DomainException("validation_failed", "Thuộc tính hoặc trạng thái vận đơn không hợp lệ.");
        if (purpose == ShipmentPurpose.Replacement && (returnRequestId is null || returnRequestId == Guid.Empty))
            throw new DomainException("validation_failed", "Vận đơn thay thế bắt buộc có mã yêu cầu đổi trả.");
        if (codAmountVnd < 0 || shippingCostVnd is < 0 || returnCostVnd is < 0)
            throw new DomainException("validation_failed", "Số tiền vận đơn không được âm.");

        return new Shipment(
            id,
            orderId,
            purpose,
            returnRequestId,
            provider,
            carrierName,
            trackingCode,
            status,
            codAmountVnd,
            shippingCostVnd,
            returnCostVnd,
            note,
            createdBy,
            createdAt,
            pickedUpAt,
            deliveredAt,
            failedAt,
            returnedAt,
            [.. events]);
    }

    // GAP-P208-02 = A & BR-SHP-05: allow updating/clearing trackingCode, shippingCostVnd, returnCostVnd
    // on any non-cancelled shipment (including delivered/failed/returned for post-reconciliation shipping cost entry).
    public void UpdateMetadata(
        bool hasTrackingCode,
        string? trackingCode,
        bool hasShippingCostVnd,
        long? shippingCostVnd,
        bool hasReturnCostVnd,
        long? returnCostVnd)
    {
        if (!hasTrackingCode && !hasShippingCostVnd && !hasReturnCostVnd)
            throw new DomainException("validation_failed", "Cần cung cấp ít nhất một trường để cập nhật vận đơn.");

        if (Status == ShipmentStatus.Cancelled)
            throw new DomainException("invalid_status_transition", "Không thể cập nhật vận đơn đã hủy.");

        var nextTrackingCode = TrackingCode;
        if (hasTrackingCode)
        {
            if (trackingCode is null)
            {
                nextTrackingCode = null;
            }
            else
            {
                // R-P208-03: PATCH rejects empty/whitespace trackingCode; explicit null is required to clear.
                var trimmed = trackingCode.Trim();
                if (trimmed.Length == 0 || trimmed.Length > MaxTrackingCodeLength || HasAnyControlChar(trackingCode))
                    throw new DomainException("validation_failed", "Mã vận đơn không hợp lệ.");
                nextTrackingCode = trimmed;
            }
        }

        if (hasShippingCostVnd && shippingCostVnd is < 0)
            throw new DomainException("validation_failed", "Chi phí giao hàng không được âm.");

        if (hasReturnCostVnd && returnCostVnd is < 0)
            throw new DomainException("validation_failed", "Chi phí hoàn hàng không được âm.");

        TrackingCode = nextTrackingCode;
        if (hasShippingCostVnd) ShippingCostVnd = shippingCostVnd;
        if (hasReturnCostVnd) ReturnCostVnd = returnCostVnd;
    }

    // GAP-P208-01 = A & GAP-P208-02 = A (Owner-approved 2026-11-26):
    // Forward non-backward transitions from created|picked_up|in_transit -> in_transit|delivered|failed
    // (picked_up only from created; returned ONLY from failed; delivered|returned|cancelled are terminal).
    public ShipmentEventEntry RecordEvent(
        string targetStatus,
        string source,
        DateTimeOffset occurredAt,
        DateTimeOffset now,
        DateTimeOffset? orderShippedAt,
        string? note,
        long? returnCostVnd,
        Guid? createdBy)
    {
        if (!ShipmentStatus.IsEventRecordable(targetStatus))
            throw new DomainException("validation_failed", "Trạng thái sự kiện vận đơn không hợp lệ.");
        if (!ShipmentEventSource.IsValid(source))
            throw new DomainException("validation_failed", "Nguồn sự kiện vận đơn không hợp lệ.");
        if (occurredAt == default || now == default)
            throw new DomainException("validation_failed", "Thời điểm sự kiện vận đơn không hợp lệ.");
        if (createdBy is { } actor && actor == Guid.Empty)
            throw new DomainException("validation_failed", "Mã nhân viên ghi nhận sự kiện không hợp lệ.");

        var normalizedNote = NormalizeOptionalNote(note);

        if (returnCostVnd.HasValue && targetStatus != ShipmentStatus.Returned)
            throw new DomainException("validation_failed", "Chi phí hoàn hàng chỉ được truyền khi sự kiện có trạng thái returned.");
        if (returnCostVnd is < 0)
            throw new DomainException("validation_failed", "Chi phí hoàn hàng không được âm.");

        if (!IsAllowedTransition(Status, targetStatus))
            throw new DomainException("invalid_status_transition", "Chuyển trạng thái vận đơn không hợp lệ.");

        if (targetStatus == ShipmentStatus.Failed && normalizedNote is null)
            throw new DomainException("failure_reason_required", "Lý do giao hàng thất bại là bắt buộc.");

        ValidateEventTimestamp(targetStatus, occurredAt, now, orderShippedAt);

        Status = targetStatus;
        switch (targetStatus)
        {
            case ShipmentStatus.PickedUp:
                PickedUpAt = occurredAt;
                break;
            case ShipmentStatus.InTransit:
                break;
            case ShipmentStatus.Delivered:
                DeliveredAt = occurredAt;
                break;
            case ShipmentStatus.Failed:
                FailedAt = occurredAt;
                break;
            case ShipmentStatus.Returned:
                ReturnedAt = occurredAt;
                if (returnCostVnd.HasValue)
                    ReturnCostVnd = returnCostVnd.Value;
                break;
        }

        return AppendEvent(targetStatus, normalizedNote, source, occurredAt, createdBy);
    }

    // GAP-P208-01 = A: Domain-only seam for P2-10 / P6-02 (created -> cancelled); no HTTP route exposed in P2-08.
    public ShipmentEventEntry Cancel(Guid? actorId, string? reason, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Created)
            throw new DomainException("invalid_status_transition", "Chỉ có thể hủy vận đơn khi đang ở trạng thái mới tạo.");
        if (actorId is { } actor && actor == Guid.Empty)
            throw new DomainException("validation_failed", "Mã người hủy vận đơn không hợp lệ.");
        if (occurredAt == default || now == default || occurredAt > now || occurredAt < CreatedAt)
            throw new DomainException("invalid_event_time", "Thời điểm hủy vận đơn không hợp lệ.");

        var normalizedReason = NormalizeOptionalNote(reason);
        Status = ShipmentStatus.Cancelled;
        return AppendEvent(ShipmentStatus.Cancelled, normalizedReason, ShipmentEventSource.Manual, occurredAt, actorId);
    }

    public IReadOnlyList<ShipmentEventEntry> DrainNewEvents()
    {
        var drained = _newEvents.ToArray();
        _newEvents.Clear();
        return drained;
    }

    private ShipmentEventEntry AppendEvent(
        string status,
        string? note,
        string source,
        DateTimeOffset occurredAt,
        Guid? createdBy)
    {
        var entry = new ShipmentEventEntry(Guid.CreateVersion7(), Id, status, note, source, occurredAt, createdBy);
        _events.Add(entry);
        _newEvents.Add(entry);
        return entry;
    }

    private static bool IsAllowedTransition(string currentStatus, string targetStatus)
    {
        if (ShipmentStatus.IsTerminal(currentStatus))
            return false;

        return targetStatus switch
        {
            ShipmentStatus.PickedUp => currentStatus == ShipmentStatus.Created,
            ShipmentStatus.InTransit => currentStatus is ShipmentStatus.Created or ShipmentStatus.PickedUp or ShipmentStatus.InTransit,
            ShipmentStatus.Delivered => currentStatus is ShipmentStatus.Created or ShipmentStatus.PickedUp or ShipmentStatus.InTransit,
            ShipmentStatus.Failed => currentStatus is ShipmentStatus.Created or ShipmentStatus.PickedUp or ShipmentStatus.InTransit,
            ShipmentStatus.Returned => currentStatus == ShipmentStatus.Failed,
            _ => false
        };
    }

    private void ValidateEventTimestamp(
        string targetStatus,
        DateTimeOffset occurredAt,
        DateTimeOffset now,
        DateTimeOffset? orderShippedAt)
    {
        switch (targetStatus)
        {
            case ShipmentStatus.Delivered:
                // 04-business-rules.md:160: delivered_at <= now AND >= shipped_at (and monotonic vs created_at / picked_up_at).
                if (occurredAt > now
                    || occurredAt < CreatedAt
                    || (orderShippedAt is { } shippedForDelivery && occurredAt < shippedForDelivery)
                    || (PickedUpAt is { } pickedForDelivery && occurredAt < pickedForDelivery))
                {
                    throw new DomainException("invalid_delivered_at", "Thời điểm giao thành công không hợp lệ.");
                }
                break;

            case ShipmentStatus.Failed:
                if (occurredAt > now
                    || occurredAt < CreatedAt
                    || (orderShippedAt is { } shippedForFail && occurredAt < shippedForFail)
                    || (PickedUpAt is { } pickedForFail && occurredAt < pickedForFail))
                {
                    throw new DomainException("invalid_event_time", "Thời điểm giao thất bại không hợp lệ.");
                }
                break;

            case ShipmentStatus.Returned:
                if (occurredAt > now
                    || occurredAt < CreatedAt
                    || (FailedAt is { } failedAt && occurredAt < failedAt))
                {
                    throw new DomainException("invalid_event_time", "Thời điểm nhận hàng hoàn về kho không hợp lệ.");
                }
                break;

            case ShipmentStatus.PickedUp:
                if (occurredAt > now || occurredAt < CreatedAt)
                {
                    throw new DomainException("invalid_event_time", "Thời điểm lấy hàng không hợp lệ.");
                }
                break;

            case ShipmentStatus.InTransit:
                if (occurredAt > now
                    || occurredAt < CreatedAt
                    || (PickedUpAt is { } pickedForTransit && occurredAt < pickedForTransit))
                {
                    throw new DomainException("invalid_event_time", "Thời điểm trung chuyển không hợp lệ.");
                }
                break;
        }
    }

    private static void ValidateCreateCommon(
        Guid id,
        Guid orderId,
        string provider,
        string carrierName,
        string? trackingCode,
        long codAmountVnd,
        long? shippingCostVnd,
        string? note,
        Guid? createdBy,
        DateTimeOffset createdAt,
        out string normalizedCarrier,
        out string? normalizedTracking,
        out string? normalizedNote)
    {
        if (id == Guid.Empty || orderId == Guid.Empty)
            throw new DomainException("validation_failed", "Định danh vận đơn hoặc đơn hàng không hợp lệ.");
        if (!ShippingProviderCode.IsValid(provider))
            throw new DomainException("validation_failed", "Nhà cung cấp vận chuyển không hợp lệ.");
        if (string.IsNullOrWhiteSpace(carrierName) || HasAnyControlChar(carrierName))
            throw new DomainException("validation_failed", "Tên đơn vị vận chuyển là bắt buộc và không chứa ký tự điều khiển.");

        normalizedCarrier = carrierName.Trim();
        if (normalizedCarrier.Length > MaxCarrierNameLength)
            throw new DomainException("validation_failed", "Tên đơn vị vận chuyển vượt quá độ dài cho phép.");

        if (string.IsNullOrWhiteSpace(trackingCode))
        {
            // R-P208-03: normalize blank trackingCode on POST to null so ux_shipment_tracking never collides on "".
            if (trackingCode is not null && HasAnyControlChar(trackingCode))
                throw new DomainException("validation_failed", "Mã vận đơn không được chứa ký tự điều khiển.");
            normalizedTracking = null;
        }
        else
        {
            if (HasAnyControlChar(trackingCode))
                throw new DomainException("validation_failed", "Mã vận đơn không được chứa ký tự điều khiển.");
            normalizedTracking = trackingCode.Trim();
            if (normalizedTracking.Length > MaxTrackingCodeLength)
                throw new DomainException("validation_failed", "Mã vận đơn vượt quá độ dài cho phép.");
        }

        if (codAmountVnd < 0)
            throw new DomainException("validation_failed", "Số tiền thu hộ COD không được âm.");
        if (shippingCostVnd is < 0)
            throw new DomainException("validation_failed", "Chi phí vận chuyển không được âm.");

        normalizedNote = NormalizeOptionalNote(note);

        if (createdBy is { } actor && actor == Guid.Empty)
            throw new DomainException("validation_failed", "Mã nhân viên tạo vận đơn không hợp lệ.");
        if (createdAt == default)
            throw new DomainException("validation_failed", "Thời điểm tạo vận đơn không hợp lệ.");
    }

    private static string? NormalizeOptionalNote(string? value)
    {
        if (value is null) return null;
        if (HasDisallowedNoteControlChar(value))
            throw new DomainException("validation_failed", "Ghi chú vận đơn chứa ký tự điều khiển không hợp lệ.");
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxNoteLength)
            throw new DomainException("validation_failed", "Ghi chú vận đơn vượt quá độ dài cho phép.");
        return trimmed;
    }

    private static bool HasAnyControlChar(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) return true;
        }
        return false;
    }

    private static bool HasDisallowedNoteControlChar(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\r')) return true;
        }
        return false;
    }
}
