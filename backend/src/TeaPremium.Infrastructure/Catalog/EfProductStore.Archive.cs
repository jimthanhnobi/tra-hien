using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfProductStore
{
    private static string ResolveArchiveTarget(Dictionary<string, string> graph, string? target, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { source };
        while (true)
        {
            if (ArchiveRoute(target) is null) throw InvalidArchiveTarget();
            if (!seen.Add(target!)) throw Rejected("redirect_conflict");
            if (!graph.TryGetValue(target!, out var next)) return target!;
            target = next;
        }
    }

    private static (string Table, string Slug, string PublicStatus)? ArchiveRoute(string? path)
    {
        // Canonical Catalog paths are ASCII slugs. Reject encoding rather than decoding ambiguous input.
        if (path is null) return null;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0].Length != 0 || !TaxonomyRules.ValidSlug(parts[2])) return null;
        return parts[1] switch
        {
            "san-pham" => ("products", parts[2], "active"),
            "danh-muc" => ("categories", parts[2], "visible"),
            "bo-suu-tap" => ("collections", parts[2], "visible"),
            "chat-lieu" => ("materials", parts[2], "published"),
            "nghe-nhan" => ("artisans", parts[2], "published"),
            _ => null
        };
    }

    private async Task<bool> PublicArchiveDestinationAsync(string path, bool protect, CancellationToken ct)
    {
        var route = ArchiveRoute(path) ?? throw InvalidArchiveTarget();
        // Materials already have the P1-03 SHARE table lock, acquired before any aggregate.
        // NOWAIT on other destination rows prevents a second aggregate lock from forming a cycle
        // with an existing parent/reference holder. Contention is a retryable API conflict.
        var rowLock = protect && route.Table != "materials" ? " FOR SHARE NOWAIT" : "";
        try
        {
            // Table is from the closed switch above; only the slug is untrusted and parameterized.
            var sql = "SELECT status AS \"Value\" FROM catalog." + route.Table + " WHERE slug=@slug" + rowLock;
            var states = await db.Database.SqlQueryRaw<string>(sql,
                new NpgsqlParameter("slug", route.Slug)).ToArrayAsync(ct);
            return states.Length == 1 && states[0] == route.PublicStatus;
        }
        catch (PostgresException exception) when (exception.SqlState == "55P03")
        { throw Rejected("concurrency_conflict"); }
        catch (InvalidOperationException exception) when (exception.InnerException is PostgresException { SqlState: "55P03" })
        {
            // Npgsql's non-retrying EF execution strategy wraps transient lock-not-available.
            // Translate only this precise condition; the command UoW rolls back the failed transaction.
            throw Rejected("concurrency_conflict");
        }
    }

    private static CatalogPersistenceException InvalidArchiveTarget() => new(ProductRules.Bad("redirectTo"));
}
