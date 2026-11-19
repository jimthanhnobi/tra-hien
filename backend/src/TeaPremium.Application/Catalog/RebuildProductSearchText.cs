namespace TeaPremium.Application.Catalog;

// Canonical P1-03 wire contract; shared by reference/taxonomy producers without
// an Application -> Infrastructure dependency. Payload and 1..200 consumer limit unchanged.
public sealed record RebuildProductSearchText(Guid[] Ids)
{
    public const string Message = "RebuildProductSearchText";
}
