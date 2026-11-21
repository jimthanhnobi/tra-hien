using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

public sealed class EfInventoryDocumentStore(AppDbContext db, IAuditContext audit) : IInventoryDocumentStore
{
    public async Task<CatalogPage<ReceiptSummary>> ReceiptsAsync(int page, int pageSize, string? status, CancellationToken ct)
    {
        var query = db.Set<Receipt>().AsNoTracking().Where(x => status == null || x.Status == status);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(x => Map(x).Summary).ToArray(), page, pageSize, total);
    }

    public async Task<CatalogPage<StocktakeSummary>> StocktakesAsync(int page, int pageSize, string? status, CancellationToken ct)
    {
        var query = db.Set<Stocktake>().AsNoTracking().Where(x => status == null || x.Status == status);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(x => Map(x).Summary).ToArray(), page, pageSize, total);
    }

    public async Task<ReceiptDocument?> ReceiptAsync(Guid id, bool locked, CancellationToken ct)
    {
        var lines = db.Set<ReceiptLine>().Where(x => x.ReceiptId == id).OrderBy(x => x.VariantId)
            .Select(x => new ReceiptLineInput(x.VariantId, x.Quantity, x.UnitCostVnd));
        if (!locked)
        {
            var found = await db.Set<Receipt>().AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { Row = x, Lines = lines.ToArray() }).AsSingleQuery().SingleOrDefaultAsync(ct);
            return found is null ? null : Map(found.Row, found.Lines);
        }
        var rows = await db.Set<Receipt>().FromSqlInterpolated(
            $"SELECT *, xmin FROM inventory.receipts WHERE id={id} FOR UPDATE").ToArrayAsync(ct);
        var row = rows.SingleOrDefault();
        return row is null ? null : Map(row, await lines.ToArrayAsync(ct));
    }

    public async Task<StocktakeDocument?> StocktakeAsync(Guid id, bool locked, CancellationToken ct)
    {
        var lines = db.Set<StocktakeLine>().Where(x => x.StocktakeId == id).OrderBy(x => x.VariantId)
            .Select(x => new StocktakeLineView(x.VariantId, x.CountedQty, x.SystemQtyAtCount, x.CountedAt, x.CountedBy));
        if (!locked)
        {
            var found = await db.Set<Stocktake>().AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { Row = x, Lines = lines.ToArray() }).AsSingleQuery().SingleOrDefaultAsync(ct);
            return found is null ? null : Map(found.Row, found.Lines);
        }
        var rows = await db.Set<Stocktake>().FromSqlInterpolated(
            $"SELECT *, xmin FROM inventory.stocktakes WHERE id={id} FOR UPDATE").ToArrayAsync(ct);
        var row = rows.SingleOrDefault();
        return row is null ? null : Map(row, await lines.ToArrayAsync(ct));
    }

    public async Task<string?> AllocateCodeAsync(bool receipt, DateOnly day, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Document allocation requires a transaction.");
        // Serialize allocators for a single document kind and Vietnam calendar day.
        var key = 910900000000L + day.DayNumber * 2L + (receipt ? 0 : 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        var prefix = (receipt ? "PN-" : "KK-") + day.ToString("yyMMdd", CultureInfo.InvariantCulture) + "-";
        var codes = receipt ? db.Set<Receipt>().Select(x => x.Code) : db.Set<Stocktake>().Select(x => x.Code);
        var existing = codes.Where(x => x.StartsWith(prefix)).AsAsyncEnumerable();
        long max = 0;
        await foreach (var code in existing.WithCancellation(ct))
        {
            var suffix = code.AsSpan(prefix.Length);
            if (suffix.IsEmpty || suffix.ContainsAnyExceptInRange('0', '9')) continue;
            suffix = suffix.TrimStart('0');
            if (suffix.IsEmpty) continue;
            if (!long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null; // Existing positive suffix exceeds the numeric range.
            max = Math.Max(max, value);
        }
        return max == long.MaxValue ? null : prefix + checked(max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    public async Task SaveReceiptAsync(ReceiptDocument document, bool create, bool linesChanged, CancellationToken ct)
    {
        var row = create
            ? new Receipt { Id = document.Id, Code = document.Code, CreatedAt = document.CreatedAt, CreatedBy = document.CreatedBy }
            : db.Set<Receipt>().Local.Single(x => x.Id == document.Id);
        if (create) db.Add(row);
        row.Status = document.Status;
        row.SupplierName = document.SupplierName;
        row.Note = document.Note;
        row.ReceivedAt = document.ReceivedAt;
        row.PostedAt = document.PostedAt;
        row.PostedBy = document.PostedBy;

        if (linesChanged)
        {
            var current = await db.Set<ReceiptLine>().Where(x => x.ReceiptId == row.Id).ToDictionaryAsync(x => x.VariantId, ct);
            foreach (var line in document.Lines)
            {
                if (!current.Remove(line.VariantId, out var entity))
                {
                    entity = new ReceiptLine { Id = Guid.CreateVersion7(), ReceiptId = row.Id, VariantId = line.VariantId };
                    db.Add(entity);
                }
                entity.Quantity = line.Quantity;
                entity.UnitCostVnd = line.UnitCostVnd!.Value;
            }
            db.RemoveRange(current.Values);
            if (!create) db.Entry(row).Property(x => x.Status).IsModified = true;
        }
        await db.SaveChangesAsync(ct);
        document.Version = row.Version;
    }

    public async Task SaveStocktakeAsync(StocktakeDocument document, bool create, StocktakeLineView? count, CancellationToken ct)
    {
        var row = create
            ? new Stocktake { Id = document.Id, Code = document.Code, CreatedAt = document.CreatedAt, CreatedBy = document.CreatedBy, Note = document.Note }
            : db.Set<Stocktake>().Local.Single(x => x.Id == document.Id);
        if (create) db.Add(row);
        row.Status = document.Status;
        row.SubmittedAt = document.SubmittedAt;
        row.ApprovedAt = document.ApprovedAt;
        row.ApprovedBy = document.ApprovedBy;
        if (count is not null)
        {
            var line = await db.Set<StocktakeLine>().FindAsync([row.Id, count.VariantId], ct);
            var previous = line is null ? null : new StocktakeLineView(line.VariantId, line.CountedQty, line.SystemQtyAtCount, line.CountedAt, line.CountedBy);
            if (line is null)
            {
                line = new StocktakeLine { StocktakeId = row.Id, VariantId = count.VariantId };
                db.Add(line);
            }
            line.CountedQty = count.CountedQty;
            line.SystemQtyAtCount = count.SystemQtyAtCount;
            line.CountedAt = count.CountedAt;
            line.CountedBy = count.CountedBy;
            db.Entry(row).Property(x => x.Status).IsModified = true;
            // The line has a composite key; use the document UUID and allowlisted count data.
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.CreateVersion7(),
                EntityType = "inventory.stocktakes",
                EntityId = row.Id,
                Action = "update",
                OccurredAt = count.CountedAt,
                ActorType = audit.ActorType,
                ActorId = audit.ActorId,
                CorrelationId = Guid.TryParse(audit.CorrelationId, out var correlation) ? correlation.ToString("D") : null,
                Changes = JsonSerializer.Serialize(new { before = previous, after = count })
            });
        }
        await db.SaveChangesAsync(ct);
        document.Version = row.Version;
    }

    public async Task<IReadOnlyDictionary<Guid, DateTime>> LatestStocktakeCorrectionsAsync(Guid[] variants, CancellationToken ct)
    {
        var query = from line in db.Set<StocktakeLine>()
                    join doc in db.Set<Stocktake>() on line.StocktakeId equals doc.Id
                    where variants.Contains(line.VariantId) && doc.Status == "approved" && doc.ApprovedAt.HasValue
                        && line.CountedQty != line.SystemQtyAtCount
                    group doc by line.VariantId into rows
                    select new { Variant = rows.Key, Latest = rows.Max(x => x.ApprovedAt!.Value) };
        return await query.ToDictionaryAsync(x => x.Variant, x => x.Latest, ct);
    }

    public Task<DateTime?> LatestPendingStocktakeCountAsync(Guid[] variants, CancellationToken ct) =>
        (from line in db.Set<StocktakeLine>()
         join doc in db.Set<Stocktake>() on line.StocktakeId equals doc.Id
         where variants.Contains(line.VariantId) && (doc.Status == "counting" || doc.Status == "submitted")
         select (DateTime?)line.CountedAt).MaxAsync(ct);

    private static ReceiptDocument Map(Receipt row, ReceiptLineInput[]? lines = null) =>
        new(row.Id, row.Code, row.CreatedAt, row.CreatedBy)
        {
            Status = row.Status,
            Version = row.Version,
            SupplierName = row.SupplierName,
            Note = row.Note,
            ReceivedAt = row.ReceivedAt,
            PostedAt = row.PostedAt,
            PostedBy = row.PostedBy,
            Lines = lines ?? []
        };

    private static StocktakeDocument Map(Stocktake row, StocktakeLineView[]? lines = null) =>
        new(row.Id, row.Code, row.CreatedAt, row.CreatedBy, row.Note)
        {
            Status = row.Status,
            Version = row.Version,
            SubmittedAt = row.SubmittedAt,
            ApprovedAt = row.ApprovedAt,
            ApprovedBy = row.ApprovedBy,
            Lines = lines ?? []
        };
}
