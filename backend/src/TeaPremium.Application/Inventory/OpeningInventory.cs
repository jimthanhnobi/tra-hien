using System.Globalization;
using System.Text.Json.Serialization;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;

namespace TeaPremium.Application.Inventory;

public sealed record OpeningInput(int Row, string Sku, string Quantity, string UnitCostVnd);
public sealed record OpeningError(string Column, string Code, string Message);
public sealed record OpeningRow(int Row, string? Sku, Guid? VariantId, IReadOnlyList<OpeningError> Errors);
public sealed record OpeningResult(bool DryRun, bool Committed, int ValidRows, int InvalidRows, IReadOnlyList<OpeningRow> Rows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviewToken = null)
{
    internal static OpeningResult From(bool dryRun, bool committed, OpeningRow[] rows) =>
        new(dryRun, committed, rows.Count(x => x.Errors.Count == 0), rows.Count(x => x.Errors.Count != 0), rows);
}

public sealed record PreviewOpening(OpeningInput[] Lines, Guid ActorId) : IQuery<OpeningResult>;
public sealed record ApplyOpening(OpeningInput[] Lines, Guid[] PreviewedVariants, Guid ActorId) : ICommand<OpeningResult>;

internal sealed record OpeningLine(OpeningInput Input, int Quantity, long Cost, OpeningError[] Errors);

internal static class OpeningValidation
{
    public static OpeningLine[] Parse(OpeningInput[] rows)
    {
        var duplicates = rows.Where(x => ProductRules.ValidSku(x.Sku)).GroupBy(x => x.Sku, StringComparer.Ordinal)
            .Where(x => x.Skip(1).Any()).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        return rows.Select(row =>
        {
            var errors = new List<OpeningError>();
            if (!ProductRules.ValidSku(row.Sku)) errors.Add(new("sku", "invalid_sku", "SKU phải đúng mã đã cấp, viết hoa."));
            else if (duplicates.Contains(row.Sku)) errors.Add(new("sku", "duplicate_sku_in_file", "SKU xuất hiện nhiều lần trong tệp."));
            if (!int.TryParse(row.Quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0
                || !row.Quantity.All(char.IsAsciiDigit))
                errors.Add(new("quantity", "invalid_quantity", "Số lượng phải là số nguyên dương trong phạm vi cho phép."));
            if (!long.TryParse(row.UnitCostVnd, NumberStyles.None, CultureInfo.InvariantCulture, out var cost)
                || !row.UnitCostVnd.All(char.IsAsciiDigit))
                errors.Add(new("unit_cost_vnd", "invalid_cost", "Giá vốn phải là số nguyên VND không âm trong phạm vi cho phép."));
            return new OpeningLine(row, quantity, cost, errors.ToArray());
        }).ToArray();
    }

    public static (OpeningRow Row, StockPosting? Posting) Plan(OpeningLine line, InventoryPosition? position,
        Guid actor, Guid? reference, bool apply)
    {
        var sku = line.Input.Sku;
        var validSku = ProductRules.ValidSku(sku) ? sku : null;
        if (line.Errors.Length != 0) return (new(line.Input.Row, validSku, null, line.Errors), null);
        if (position is null || !string.Equals(sku, position.Variant.Sku, StringComparison.Ordinal))
            return (new(line.Input.Row, sku, null, [apply
                ? new("sku", "sku_identity_changed", "SKU đã thay đổi từ lúc chạy thử. Hãy chạy thử lại.")
                : new("sku", "sku_not_found", "Không tìm thấy SKU.")]), null);

        var error = InventoryMutation.Plan(position,
            new StockChange("opening", line.Quantity, line.Cost, "import", reference, null, "staff", actor), out var posting);
        return (new(line.Input.Row, sku, position.Variant.Id, error is null ? [] : [error switch
        {
            "opening_already_recorded" => new("sku", error, "SKU đã có biến động kho."),
            "unique_piece_violation" => new("quantity", error, "Tồn hàng độc bản không được vượt quá một."),
            _ => new("quantity", error, "Không thể áp dụng số lượng này.")
        }]), posting);
    }
}

public sealed class PreviewOpeningHandler(IInventoryStockStore stock) : IQueryHandler<PreviewOpening, OpeningResult>
{
    public async Task<Result<OpeningResult>> Handle(PreviewOpening query, CancellationToken cancellationToken)
    {
        var lines = OpeningValidation.Parse(query.Lines);
        var states = await stock.PreviewAsync(lines.Where(x => x.Errors.Length == 0).Select(x => x.Input.Sku).ToArray(), cancellationToken);
        var rows = lines.Select(x => OpeningValidation.Plan(x, states.GetValueOrDefault(x.Input.Sku),
            query.ActorId, null, false).Row).ToArray();
        return Result.Success(OpeningResult.From(true, false, rows));
    }
}

public sealed class ApplyOpeningHandler(IInventoryStockStore stock, InventoryMutation mutation) : ICommandHandler<ApplyOpening, OpeningResult>
{
    public async Task<Result<OpeningResult>> Handle(ApplyOpening command, CancellationToken cancellationToken)
    {
        var lines = OpeningValidation.Parse(command.Lines);
        if (lines.Length == 0 || lines.Length != command.PreviewedVariants.Length
            || command.PreviewedVariants.Distinct().Count() != lines.Length)
            return Result.Failure<OpeningResult>(new("opening_preview_invalid", "Hãy chạy thử lại tệp.", ErrorType.Validation));

        var states = await stock.LockAsync(command.PreviewedVariants, cancellationToken);
        var reference = Guid.CreateVersion7();
        var plans = lines.Select((line, i) => OpeningValidation.Plan(line,
            states.GetValueOrDefault(command.PreviewedVariants[i]), command.ActorId, reference, true)).ToArray();
        var commit = plans.All(x => x.Posting is not null);
        if (commit) mutation.Stage(plans.Select(x => x.Posting!).ToArray());
        return Result.Success(OpeningResult.From(false, commit, plans.Select(x => x.Row).ToArray()));
    }
}
