using System.Text.Json.Serialization;
using TeaPremium.Application.Catalog;

namespace TeaPremium.Application.Inventory;

public sealed record ReceiptLineInput(Guid VariantId, int Quantity, long? UnitCostVnd = null);
public sealed record ReceiptLineView(Guid VariantId, int Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UnitCostVnd = null);
public sealed record ReceiptSummary(Guid Id, string Code, string Status, string? SupplierName, string? Note,
    DateTime? ReceivedAt, DateTime? PostedAt, Guid? PostedBy, DateTime CreatedAt, Guid CreatedBy, string RowVersion);
public sealed record ReceiptView(ReceiptSummary Document, IReadOnlyList<ReceiptLineView> Lines);
public sealed record StocktakeLineView(Guid VariantId, int CountedQty, int SystemQtyAtCount, DateTime CountedAt, Guid CountedBy);
public sealed record StocktakeSummary(Guid Id, string Code, string Status, string? Note, DateTime CreatedAt,
    Guid CreatedBy, DateTime? SubmittedAt, DateTime? ApprovedAt, Guid? ApprovedBy, string RowVersion);
public sealed record StocktakeView(StocktakeSummary Document, IReadOnlyList<StocktakeLineView> Lines);
public sealed record ReceiptPosted(Guid Id, string Status, string RowVersion);
public sealed record AdjustmentView(Guid Id, Guid VariantId, int QuantityDelta, int OnHandAfter);

public sealed record ReceiptDraft(string? SupplierName, string? Note, DateTime? ReceivedAt, ReceiptLineInput[]? Lines);
public sealed record ReceiptEdit(ReceiptDraft Draft, bool HasSupplier, bool HasNote, bool HasReceivedAt);

public sealed record ReceiptDocument(Guid Id, string Code, DateTime CreatedAt, Guid CreatedBy)
{
    public uint Version { get; set; }
    public string Status { get; set; } = "draft";
    public string? SupplierName { get; set; }
    public string? Note { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public Guid? PostedBy { get; set; }
    public ReceiptLineInput[] Lines { get; set; } = [];
    public ReceiptSummary Summary => new(Id, Code, Status, SupplierName, Note, ReceivedAt, PostedAt, PostedBy, CreatedAt, CreatedBy, CatalogVersions.Encode(Version));
    public ReceiptView View(bool costs) => new(Summary, Lines.Select(x => new ReceiptLineView(x.VariantId, x.Quantity, costs ? x.UnitCostVnd : null)).ToArray());
}

public sealed record StocktakeDocument(Guid Id, string Code, DateTime CreatedAt, Guid CreatedBy, string? Note)
{
    public uint Version { get; set; }
    public string Status { get; set; } = "counting";
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public StocktakeLineView[] Lines { get; set; } = [];
    public StocktakeSummary Summary => new(Id, Code, Status, Note, CreatedAt, CreatedBy, SubmittedAt, ApprovedAt, ApprovedBy, CatalogVersions.Encode(Version));
    public StocktakeView View => new(Summary, Lines);
}

public interface IInventoryDocumentStore
{
    Task<CatalogPage<ReceiptSummary>> ReceiptsAsync(int page, int pageSize, string? status, CancellationToken ct);
    Task<CatalogPage<StocktakeSummary>> StocktakesAsync(int page, int pageSize, string? status, CancellationToken ct);
    Task<ReceiptDocument?> ReceiptAsync(Guid id, bool locked, CancellationToken ct);
    Task<StocktakeDocument?> StocktakeAsync(Guid id, bool locked, CancellationToken ct);
    Task<string?> AllocateCodeAsync(bool receipt, DateOnly day, CancellationToken ct);
    Task SaveReceiptAsync(ReceiptDocument document, bool create, bool linesChanged, CancellationToken ct);
    Task SaveStocktakeAsync(StocktakeDocument document, bool create, StocktakeLineView? count, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, DateTime>> LatestStocktakeCorrectionsAsync(Guid[] variants, CancellationToken ct);
    Task<DateTime?> LatestPendingStocktakeCountAsync(Guid[] variants, CancellationToken ct);
}
