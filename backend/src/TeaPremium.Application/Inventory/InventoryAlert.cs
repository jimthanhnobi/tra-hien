using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeaPremium.Application.Inventory;

// BR-INV-11 / BR-INV-13: internal alert outbox contract. Never carries PII or cost fields;
// consumer re-queries current persisted state idempotently.
public sealed record InventoryAlert(
    string Kind,
    Guid VariantId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? VietnamDate = null)
{
    public const string Message = "InventoryAlert";
    public const string LowStock = "low_stock";
    public const string ReconciliationMismatch = "reconciliation_mismatch";

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string LowStockDeduplicationKey(Guid variantId) =>
        $"{Message}:{LowStock}:{variantId:D}";

    public static string Serialize(InventoryAlert alert) =>
        JsonSerializer.Serialize(alert, WriteOptions);

    public static bool TryParse(string? payload, out InventoryAlert alert)
    {
        alert = null!;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var parsed = document.RootElement.Deserialize<InventoryAlert>(ReadOptions);
            if (parsed is null || parsed.VariantId == Guid.Empty
                || parsed.Kind is not (LowStock or ReconciliationMismatch)
                || parsed.VietnamDate == DateOnly.MinValue)
                return false;
            alert = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
