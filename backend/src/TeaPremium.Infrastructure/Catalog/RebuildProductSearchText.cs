using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

// BR-CAT-10: idempotent redelivery recalculates from current persisted names, never stale payload names.
public sealed class RebuildProductSearchTextHandler(AppDbContext db) : IOutboxHandler
{
    public string MessageType => RebuildProductSearchText.Message;
    public async Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<RebuildProductSearchText>(payload)
            ?? throw new InvalidOperationException("Invalid search rebuild payload.");
        if (message.Ids is null || message.Ids.Length is < 1 or > 200) throw new InvalidOperationException("Invalid search rebuild batch.");
        await RebuildAsync(db, message.Ids, cancellationToken);
    }
    internal static async Task RebuildAsync(AppDbContext db, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length > 1)
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Bulk search rebuild requires the outbox transaction.");

            // PostgreSQL UUID order matches the Inventory parent lock query.
            await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM catalog.products WHERE id=ANY({ids}) ORDER BY id FOR NO KEY UPDATE")
                .ToArrayAsync(cancellationToken);
        }

        // Correlated lookups also handle products without material/artisan.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.products p SET search_text = public.f_unaccent(concat_ws(' ', p.name, p.subtitle,
                (SELECT string_agg(concat_ws(' ', v.sku, v.name), ' ' ORDER BY v.position, v.id)
                 FROM catalog.variants v WHERE v.product_id = p.id),
                (SELECT a.name FROM catalog.artisans a WHERE a.id = p.artisan_id),
                (SELECT m.name FROM catalog.materials m WHERE m.id = p.material_id),
                (SELECT t.name FROM catalog.product_types t WHERE t.id = p.product_type_id)))
            WHERE p.id = ANY({ids})
            """, cancellationToken);
    }
}
