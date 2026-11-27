using TeaPremium.Application.Orders;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Infrastructure.Orders;

// 02-architecture.md §5.6 / FR-SHP-04 / DEC-09: manual shipping provider (P2-08); GHN/GHTK arrive in P6-02.
public sealed class ManualShippingProvider : IShippingProvider
{
    public string Code => ShippingProviderCode.Manual;

    public Task<ShippingQuote?> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct) =>
        Task.FromResult<ShippingQuote?>(null);

    public Task<CreateProviderShipmentResult> CreateAsync(CreateProviderShipmentRequest request, CancellationToken ct)
    {
        var carrierName = request.CarrierName.Trim();
        var trackingCode = string.IsNullOrWhiteSpace(request.TrackingCode) ? null : request.TrackingCode.Trim();
        return Task.FromResult(new CreateProviderShipmentResult(Code, carrierName, trackingCode, request.ShippingCostVnd));
    }

    public Task<ShippingWebhookResult> HandleWebhookAsync(ShippingWebhookPayload payload, CancellationToken ct) =>
        throw new NotSupportedException("Manual shipping provider does not support inbound webhooks.");
}
