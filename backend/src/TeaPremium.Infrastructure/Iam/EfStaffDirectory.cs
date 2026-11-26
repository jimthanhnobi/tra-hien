using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Iam;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Iam;

public sealed class EfStaffDirectory(AppDbContext db) : IStaffDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(IReadOnlyCollection<Guid> staffIds, CancellationToken ct)
    {
        if (staffIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var distinct = staffIds.Distinct().ToArray();
        var rows = await db.Set<StaffUser>().AsNoTracking()
            .Where(x => distinct.Contains(x.Id))
            .Select(x => new { x.Id, x.DisplayName })
            .ToArrayAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x.DisplayName);
    }
}
