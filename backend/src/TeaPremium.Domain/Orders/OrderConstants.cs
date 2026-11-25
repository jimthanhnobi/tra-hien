namespace TeaPremium.Domain.Orders;

public static class OrderOrigin
{
    public const string Web = "web";
    public const string Admin = "admin";

    public static bool IsValid(string? value) => value is Web or Admin;
}

public static class SalesChannel
{
    public const string Web = "web";
    public const string ZaloPersonal = "zalo_personal";
    public const string ZaloOa = "zalo_oa";
    public const string Facebook = "facebook";
    public const string Phone = "phone";
    public const string InPerson = "in_person";
    public const string Other = "other";

    public static bool IsValid(string? value) => value is Web or ZaloPersonal or ZaloOa or Facebook or Phone or InPerson or Other;

    public static bool IsValidForAdmin(string? value) => IsValid(value) && value != Web;
}

public static class OrderStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Shipping = "shipping";
    public const string Delivered = "delivered";
    public const string DeliveryFailed = "delivery_failed";
    public const string Cancelled = "cancelled";

    public static bool IsValid(string? value) =>
        value is Pending or Confirmed or Shipping or Delivered or DeliveryFailed or Cancelled;

    public static bool IsTerminal(string? value) =>
        value is Delivered or DeliveryFailed or Cancelled;

    public static bool AllowsLineMutation(string? value) =>
        value is Pending or Confirmed;
}

public static class OrderPaymentMethod
{
    public const string Cod = "cod";
    public const string BankTransfer = "bank_transfer";

    public static bool IsValid(string? value) => value is Cod or BankTransfer;
}

public static class OrderPaymentStatus
{
    public const string Unpaid = "unpaid";
    public const string PartiallyPaid = "partially_paid";
    public const string Paid = "paid";
    public const string PartiallyRefunded = "partially_refunded";
    public const string Refunded = "refunded";

    public static bool IsValid(string? value) =>
        value is Unpaid or PartiallyPaid or Paid or PartiallyRefunded or Refunded;
}

public static class OrderPriceSource
{
    public const string List = "list";
    public const string Sale = "sale";
    public const string Override = "override";

    public static bool IsValid(string? value) => value is List or Sale or Override;
}

public static class OrderActorType
{
    public const string Staff = "staff";
    public const string Customer = "customer";
    public const string System = "system";

    public static bool IsValid(string? value) => value is Staff or Customer or System;
}

public static class ReservationStatus
{
    public const string Active = "active";
    public const string Consumed = "consumed";
    public const string Released = "released";

    public static bool IsValid(string? value) => value is Active or Consumed or Released;
}

public static class ReservationCloseReason
{
    public const string OrderCancelled = "order_cancelled";
    public const string PaymentExpired = "payment_expired";
    public const string LineRemoved = "line_removed";
    public const string QuantityReduced = "quantity_reduced";
    public const string Shipped = "shipped";
}

public static class OrderAcquisitionSource
{
    public const string Unknown = "unknown";
    public const string Legacy = "legacy";
    public const string GoogleOrganic = "google_organic";
    public const string GoogleAds = "google_ads";
    public const string Facebook = "facebook";
    public const string ZaloPersonal = "zalo_personal";
    public const string ZaloOa = "zalo_oa";
    public const string Referral = "referral";
    public const string Direct = "direct";
    public const string Offline = "offline";
    public const string Other = "other";

    public static bool IsValid(string? value) => value is Unknown or Legacy or GoogleOrganic or GoogleAds
        or Facebook or ZaloPersonal or ZaloOa or Referral or Direct or Offline or Other;
}

public static class OrderEventTypes
{
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string OrderCancelled = "OrderCancelled";
    public const string OrderShipped = "OrderShipped";
    public const string OrderDelivered = "OrderDelivered";
    public const string OrderDeliveryFailed = "OrderDeliveryFailed";

    public static readonly string[] All =
    [
        OrderPlaced,
        OrderConfirmed,
        OrderCancelled,
        OrderShipped,
        OrderDelivered,
        OrderDeliveryFailed
    ];

    public static bool IsValid(string? value) => value is OrderPlaced or OrderConfirmed or OrderCancelled
        or OrderShipped or OrderDelivered or OrderDeliveryFailed;
}
