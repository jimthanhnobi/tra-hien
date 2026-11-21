using System.Text;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;

namespace TeaPremium.Application.Inventory;

public sealed class InventoryDocuments(IInventoryDocumentStore documents, IInventoryStockStore stock,
    InventoryMutation mutation, IUnitOfWork uow, IClock clock)
{
    public const int MaxLines = 500;
    private static Error Missing => new("not_found", "Không tìm thấy dữ liệu.", ErrorType.NotFound);
    private static Error WrongState => new("invalid_status_transition", "Trạng thái chứng từ không cho phép thao tác.", ErrorType.Conflict);
    private static Error Stale => new("concurrency_conflict", "Chứng từ đã thay đổi. Hãy tải lại.", ErrorType.Conflict);
    private static Error Forbidden => new("forbidden", "Bạn không có quyền thực hiện thao tác này.", ErrorType.Forbidden);

    public Task<Result<ReceiptView>> CreateReceiptAsync(ReceiptDraft input, Guid actor, bool costs, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var now = clock.UtcNow;
            var code = await documents.AllocateCodeAsync(true, BusinessCalendar.ToVietnamDate(now), token);
            if (code is null) return Fail<ReceiptView>(CodeExhausted());
            var doc = new ReceiptDocument(Guid.CreateVersion7(), code, now.UtcDateTime, actor);
            return await WriteReceiptAsync(doc, new(input, true, true, true), true, costs, token);
        }, ct);

    public Task<Result<ReceiptView>> EditReceiptAsync(Guid id, string? version, ReceiptEdit edit, bool costs, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var doc = await documents.ReceiptAsync(id, true, token);
            if (doc is null) return Fail<ReceiptView>(Missing);
            if (CheckVersion(version, doc.Version) is { } error) return Fail<ReceiptView>(error);
            if (doc.Status != "draft") return Fail<ReceiptView>(WrongState);
            return await WriteReceiptAsync(doc, edit, false, costs, token);
        }, ct);

    private async Task<Result<ReceiptView>> WriteReceiptAsync(ReceiptDocument doc, ReceiptEdit edit, bool create, bool costs, CancellationToken ct)
    {
        var input = edit.Draft;
        if (!ValidText(input.SupplierName, 200)) return Fail<ReceiptView>(Invalid("supplierName"));
        if (!ValidText(input.Note, 2000)) return Fail<ReceiptView>(Invalid("note"));
        if (input.Lines is { } lines)
        {
            if (lines.Length > MaxLines || lines.Any(x => x is null || x.VariantId == Guid.Empty || x.Quantity <= 0 || x.UnitCostVnd < 0)
                || lines.Select(x => x.VariantId).Distinct().Count() != lines.Length)
                return Fail<ReceiptView>(Invalid("lines"));
            var old = doc.Lines.ToDictionary(x => x.VariantId);
            doc.Lines = lines.Select(x => x with { UnitCostVnd = x.UnitCostVnd ?? old.GetValueOrDefault(x.VariantId)?.UnitCostVnd }).ToArray();
            if (doc.Lines.Any(x => x.UnitCostVnd is null)) return Fail<ReceiptView>(Invalid("lines.unitCostVnd"));
            var positions = await stock.LockAsync(lines.Select(x => x.VariantId).ToArray(), ct);
            if (positions.Count != lines.Length) return Fail<ReceiptView>(Invalid("lines.variantId"));
        }
        if (edit.HasSupplier) doc.SupplierName = Clean(input.SupplierName);
        if (edit.HasNote) doc.Note = Clean(input.Note);
        if (edit.HasReceivedAt) doc.ReceivedAt = input.ReceivedAt;
        await documents.SaveReceiptAsync(doc, create, input.Lines is not null, ct);
        return Result.Success(doc.View(costs));
    }

    public Task<Result<ReceiptView>> CancelReceiptAsync(Guid id, string? version, bool costs, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var doc = await documents.ReceiptAsync(id, true, token);
            if (doc is null) return Fail<ReceiptView>(Missing);
            if (CheckVersion(version, doc.Version) is { } error) return Fail<ReceiptView>(error);
            if (doc.Status != "draft") return Fail<ReceiptView>(WrongState);
            doc.Status = "cancelled";
            await documents.SaveReceiptAsync(doc, false, false, token);
            return Result.Success(doc.View(costs));
        }, ct);

    public Task<Result<ReceiptPosted>> PostReceiptAsync(Guid id, string? version, Guid actor, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var doc = await documents.ReceiptAsync(id, true, token);
            if (doc is null) return Fail<ReceiptPosted>(Missing);
            if (CheckVersion(version, doc.Version) is { } error) return Fail<ReceiptPosted>(error);
            if (doc.Status != "draft") return Fail<ReceiptPosted>(WrongState);
            if (doc.Lines.Length == 0) return Fail<ReceiptPosted>(Invalid("lines"));
            var positions = await stock.LockAsync(doc.Lines.Select(x => x.VariantId).ToArray(), token);
            var postings = new List<StockPosting>();
            var failures = new Dictionary<string, string[]>();
            foreach (var line in doc.Lines)
                Plan(positions, line.VariantId, new("receipt", line.Quantity, line.UnitCostVnd, "receipt", id, null, "staff", actor), postings, failures);
            if (failures.Count != 0) return Fail<ReceiptPosted>(LineFailure(failures, "receipt_lines_invalid"));
            doc.Status = "posted";
            doc.PostedAt = clock.UtcNow.UtcDateTime;
            doc.PostedBy = actor;
            mutation.Stage(postings);
            await documents.SaveReceiptAsync(doc, false, false, token);
            return Result.Success(new ReceiptPosted(id, doc.Status, CatalogVersions.Encode(doc.Version)));
        }, ct);

    public Task<Result<AdjustmentView>> AdjustAsync(Guid id, int delta, string? type, string? reason, Guid actor, bool approve, CancellationToken ct)
    {
        if (delta > 0 && !approve) return Task.FromResult(Fail<AdjustmentView>(Forbidden));
        reason = Clean(reason);
        if (id == Guid.Empty || delta == 0 || type is not ("damage" or "adjustment") || type == "damage" && delta > 0
            || reason is null || reason.EnumerateRunes().Count() < 3 || !ValidText(reason, 2000))
            return Task.FromResult(Fail<AdjustmentView>(Invalid("adjustment")));
        return uow.ExecuteAsync(async token =>
        {
            var positions = await stock.LockAsync([id], token);
            if (!positions.TryGetValue(id, out var position)) return Fail<AdjustmentView>(Missing);
            var reference = Guid.CreateVersion7();
            var error = InventoryMutation.Plan(position, new(type, delta, position.Balance.AvgCostVnd,
                "adjustment", reference, reason, "staff", actor), out var posting);
            if (error is not null) return Fail<AdjustmentView>(MovementFailure(error));
            mutation.Stage([posting!]);
            return Result.Success(new AdjustmentView(reference, id, delta, posting!.BalanceAfter.OnHand));
        }, ct);
    }

    public Task<Result<StocktakeView>> CreateStocktakeAsync(string? note, Guid actor, CancellationToken ct)
    {
        if (!ValidText(note, 2000)) return Task.FromResult(Fail<StocktakeView>(Invalid("note")));
        return uow.ExecuteAsync(async token =>
        {
            var now = clock.UtcNow;
            var code = await documents.AllocateCodeAsync(false, BusinessCalendar.ToVietnamDate(now), token);
            if (code is null) return Fail<StocktakeView>(CodeExhausted());
            var doc = new StocktakeDocument(Guid.CreateVersion7(), code, now.UtcDateTime, actor, Clean(note));
            await documents.SaveStocktakeAsync(doc, true, null, token);
            return Result.Success(doc.View);
        }, ct);
    }

    public Task<Result<StocktakeView>> CountAsync(Guid id, string? version, Guid variantId, int quantity, Guid actor, CancellationToken ct)
    {
        if (variantId == Guid.Empty || quantity < 0) return Task.FromResult(Fail<StocktakeView>(Invalid("countedQty")));
        return uow.ExecuteAsync(async token =>
        {
            var doc = await documents.StocktakeAsync(id, true, token);
            if (doc is null) return Fail<StocktakeView>(Missing);
            if (CheckVersion(version, doc.Version) is { } error) return Fail<StocktakeView>(error);
            if (doc.Status != "counting") return Fail<StocktakeView>(WrongState);
            if (doc.Lines.Length >= MaxLines && doc.Lines.All(x => x.VariantId != variantId))
                return Fail<StocktakeView>(Invalid("lines"));
            var state = await stock.LockAsync([variantId], token);
            if (!state.TryGetValue(variantId, out var position)) return Fail<StocktakeView>(Missing);
            var prior = (await documents.LatestStocktakeCorrectionsAsync([variantId], token)).GetValueOrDefault(variantId);
            var line = new StocktakeLineView(variantId, quantity, position.Balance.OnHand, After(prior), actor);
            doc.Lines = [.. doc.Lines.Where(x => x.VariantId != variantId), line];
            await documents.SaveStocktakeAsync(doc, false, line, token);
            return Result.Success(doc.View);
        }, ct);
    }

    public Task<Result<StocktakeView>> TransitionStocktakeAsync(Guid id, string? version, string action, Guid actor, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var doc = await documents.StocktakeAsync(id, true, token);
            if (doc is null) return Fail<StocktakeView>(Missing);
            if (CheckVersion(version, doc.Version) is { } error) return Fail<StocktakeView>(error);
            switch (action, doc.Status)
            {
                case ("submit", "counting"):
                    if (doc.Lines.Length == 0) return Fail<StocktakeView>(Invalid("lines"));
                    doc.Status = "submitted";
                    doc.SubmittedAt = After(doc.Lines.Max(x => x.CountedAt));
                    break;
                case ("cancel", "counting" or "submitted"):
                    doc.Status = "cancelled";
                    break;
                case ("approve", "submitted"):
                    if (await ApproveAsync(doc, actor, token) is { } denied) return Fail<StocktakeView>(denied);
                    break;
                default: return Fail<StocktakeView>(WrongState);
            }
            await documents.SaveStocktakeAsync(doc, false, null, token);
            return Result.Success(doc.View);
        }, ct);

    private async Task<Error?> ApproveAsync(StocktakeDocument doc, Guid actor, CancellationToken ct)
    {
        if (doc.Lines.Length == 0) return Invalid("lines");
        var variants = doc.Lines.Select(x => x.VariantId).ToArray();
        var positions = await stock.LockAsync(variants, ct);
        var affected = doc.Lines.Where(x => x.CountedQty != x.SystemQtyAtCount).Select(x => x.VariantId).ToArray();
        var corrections = await documents.LatestStocktakeCorrectionsAsync(affected, ct);
        var postings = new List<StockPosting>();
        var failures = new Dictionary<string, string[]>();
        foreach (var line in doc.Lines)
        {
            if (!positions.TryGetValue(line.VariantId, out var position))
                failures[LineField(line.VariantId)] = ["not_found"];
            else if (line.CountedQty != line.SystemQtyAtCount)
            {
                if (corrections.GetValueOrDefault(line.VariantId) >= line.CountedAt)
                    failures[LineField(line.VariantId)] = ["stocktake_count_stale"];
                else
                    Plan(positions, line.VariantId, new("stocktake", line.CountedQty - line.SystemQtyAtCount,
                        position.Balance.AvgCostVnd, "stocktake", doc.Id, null, "staff", actor), postings, failures);
            }
        }
        if (failures.Count != 0) return LineFailure(failures, "stocktake_lines_invalid");
        // Coordinated by variant locks. Keep comparisons strict at PostgreSQL microsecond precision,
        // including concurrent instances and readings that share the same system-clock tick.
        var lastCount = await documents.LatestPendingStocktakeCountAsync(affected, ct);
        doc.Status = "approved";
        var submittedAt = doc.SubmittedAt ?? doc.CreatedAt;
        doc.ApprovedAt = After(lastCount > submittedAt ? lastCount.Value : submittedAt);
        doc.ApprovedBy = actor;
        mutation.Stage(postings);
        return null;
    }

    private DateTime After(DateTime previous)
    {
        var now = clock.UtcNow.UtcDateTime;
        var floor = previous.AddTicks(10);
        return now >= floor ? now : floor;
    }

    private static void Plan(IReadOnlyDictionary<Guid, InventoryPosition> states, Guid id, StockChange change,
        List<StockPosting> postings, Dictionary<string, string[]> failures)
    {
        if (!states.TryGetValue(id, out var position)) { failures[LineField(id)] = ["not_found"]; return; }
        var error = InventoryMutation.Plan(position, change, out var posting);
        if (error is null) postings.Add(posting!);
        else failures[LineField(id)] = [error];
    }

    private static string LineField(Guid id) => $"lines[{id:D}]";
    private static Error LineFailure(Dictionary<string, string[]> errors, string fallback)
    {
        var codes = errors.Values.Select(x => x[0]).Distinct(StringComparer.Ordinal).ToArray();
        var code = codes.Length == 1 && codes[0] is "unique_piece_violation" or "out_of_stock" ? codes[0] : fallback;
        return new(code, "Có dòng không thể ghi vào sổ kho.", ErrorType.Conflict, errors);
    }
    private static Error MovementFailure(string code) => new(code, "Không thể áp dụng biến động kho.", ErrorType.Conflict);
    private static Error CodeExhausted() => new("inventory_document_codes_exhausted", "Đã hết mã chứng từ trong ngày.", ErrorType.Conflict);
    private static Error? CheckVersion(string? supplied, uint current) =>
        !CatalogVersions.TryDecode(supplied, out var version) ? Invalid("rowVersion") : version == current ? null : Stale;
    public static Error Invalid(string field) => ProductRules.Bad(field);
    private static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);
    private static bool ValidText(string? value, int max) => value is null || value.Length <= max && !value.Contains('\0');
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
