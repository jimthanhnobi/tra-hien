namespace TeaPremium.Application.Orders;

// 02-architecture.md §5.6 / FR-SHP-04 / DEC-09: shipping provider abstraction (manual in P2-08; GHN/GHTK in P6-02).
public sealed record ShippingQuoteRequest(
    Guid OrderId,
    string ProvinceCode,
    string? WardCode,
    int TotalWeightGrams,
    long MerchandiseVnd,
    long CodAmountVnd);

public sealed record ShippingQuote(
    string ProviderCode,
    string CarrierName,
    long FeeVnd,
    int? EstimatedDaysMin,
    int? EstimatedDaysMax);

public sealed record CreateProviderShipmentRequest(
    Guid OrderId,
    string OrderCode,
    string Purpose,
    string CarrierName,
    string? TrackingCode,
    long CodAmountVnd,
    long? ShippingCostVnd,
    string? Note);

public sealed record CreateProviderShipmentResult(
    string ProviderCode,
    string CarrierName,
    string? TrackingCode,
    long? ShippingCostVnd);

public sealed record ShippingWebhookPayload(
    string ProviderCode,
    string RawBody,
    IReadOnlyDictionary<string, string> Headers);

public sealed record ShippingWebhookResult(
    bool Accepted,
    string? ExternalEventId,
    string? Message);

public interface IShippingProvider
{
    string Code { get; }
    Task<ShippingQuote?> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct);
    Task<CreateProviderShipmentResult> CreateAsync(CreateProviderShipmentRequest request, CancellationToken ct);
    Task<ShippingWebhookResult> HandleWebhookAsync(ShippingWebhookPayload payload, CancellationToken ct);
}
