namespace TeaPremium.Domain.Orders;

public static class ShipmentPurpose
{
    public const string Order = "order";
    public const string Replacement = "replacement";

    public static readonly string[] All = [Order, Replacement];

    public static bool IsValid(string? value) => value is Order or Replacement;
}

public static class ShippingProviderCode
{
    public const string Manual = "manual";
    public const string Ghn = "ghn";
    public const string Ghtk = "ghtk";

    public static readonly string[] All = [Manual, Ghn, Ghtk];

    public static bool IsValid(string? value) => value is Manual or Ghn or Ghtk;
}

public static class ShipmentStatus
{
    public const string Created = "created";
    public const string PickedUp = "picked_up";
    public const string InTransit = "in_transit";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Returned = "returned";
    public const string Cancelled = "cancelled";

    public static readonly string[] All =
    [
        Created,
        PickedUp,
        InTransit,
        Delivered,
        Failed,
        Returned,
        Cancelled
    ];

    // GAP-P208-01 = A (Owner-approved 2026-11-26): HTTP event route accepts strictly these 5 statuses (no HTTP cancelled route).
    public static readonly string[] EventRecordable =
    [
        PickedUp,
        InTransit,
        Delivered,
        Failed,
        Returned
    ];

    public static bool IsValid(string? value) =>
        value is Created or PickedUp or InTransit or Delivered or Failed or Returned or Cancelled;

    public static bool IsEventRecordable(string? value) =>
        value is PickedUp or InTransit or Delivered or Failed or Returned;

    public static bool IsTerminal(string? value) =>
        value is Delivered or Returned or Cancelled;

    // Matches ux_shipment_active_order predicate: status NOT IN ('cancelled','failed','returned').
    public static bool IsActiveForOrder(string? value) =>
        value is Created or PickedUp or InTransit or Delivered;
}

public static class ShipmentEventSource
{
    public const string Manual = "manual";
    public const string Webhook = "webhook";

    public static readonly string[] All = [Manual, Webhook];

    public static bool IsValid(string? value) => value is Manual or Webhook;
}
