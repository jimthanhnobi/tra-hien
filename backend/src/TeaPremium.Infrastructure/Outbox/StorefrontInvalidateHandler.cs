using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.Outbox;

namespace TeaPremium.Infrastructure.Outbox;

public sealed class StorefrontInvalidateHandler(HttpClient client, IConfiguration configuration) : IOutboxHandler
{
    public string MessageType => StorefrontInvalidate.Message;
    public async Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<StorefrontInvalidate>(payload);
        if (message?.Tags is not { Length: > 0 and <= 200 } || message.Tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200 || x.Any(char.IsControl)))
            throw new InvalidOperationException("Invalid invalidation payload.");
        var address = configuration["STORE_REVALIDATE_URL"];
        var secret = configuration["REVALIDATE_SECRET"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Storefront invalidation is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { tags = message.Tags.Distinct(StringComparer.Ordinal).ToArray() }) };
        request.Headers.Add("X-Revalidate-Secret", secret);
        using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
    }
}
