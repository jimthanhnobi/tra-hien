namespace TeaPremium.Application.Iam;

public interface IStaffDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(IReadOnlyCollection<Guid> staffIds, CancellationToken ct);
}
