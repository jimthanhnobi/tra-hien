using System.Text.Json;
using TeaPremium.Application.Outbox;

namespace TeaPremium.Infrastructure.Catalog;

internal static class ProductInvalidation
{
    public static void Enqueue(IOutboxWriter outbox, string slug, IEnumerable<string> categorySlugs, string? oldSlug = null)
    {
        var tags = new[] { "product:" + slug, "product:" + (oldSlug ?? slug), "catalog", "sitemap", "feed" }
            .Concat(categorySlugs.Select(value => "category:" + value)).Distinct(StringComparer.Ordinal);
        foreach (var batch in tags.Chunk(200))
            outbox.Enqueue(StorefrontInvalidate.Message, JsonSerializer.Serialize(new StorefrontInvalidate(batch)));
    }
}
