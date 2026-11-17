using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Settings;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Settings;

public sealed class EfSettingsStore(AppDbContext db) : ISettingsStore
{
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string group, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
        {
            if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Settings update requires the command transaction.");
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE platform.settings IN SHARE ROW EXCLUSIVE MODE", ct);
        }
        var query = db.Settings.Where(x => x.Key.StartsWith(group + "."));
        if (!forUpdate) query = query.AsNoTracking();
        return await query.ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.Ordinal, ct);
    }

    public void SetValue(string key, string value, Guid actorId, DateTime updatedAt)
    {
        var entity = db.Settings.Local.SingleOrDefault(x => x.Key == key);
        if (entity is null) { entity = new Setting { Key = key }; db.Settings.Add(entity); }
        entity.Value = value;
        entity.UpdatedBy = actorId;
        entity.UpdatedAt = updatedAt;
    }
}
