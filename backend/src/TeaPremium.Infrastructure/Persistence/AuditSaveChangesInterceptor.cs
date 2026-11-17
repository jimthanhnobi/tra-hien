using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using TeaPremium.Application.Time;
using TeaPremium.Domain;

namespace TeaPremium.Infrastructure.Persistence;

public sealed class AuditSaveChangesInterceptor(IClock clock, IAuditContext auditContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db)
        {
            db.ChangeTracker.DetectChanges();
            if (AuditedChanges(db).Any())
                throw new InvalidOperationException("Synchronous SaveChanges is not supported for audited changes.");
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var db = eventData.Context;
        if (db is null) return result;

        db.ChangeTracker.DetectChanges();
        // Snapshot before adding audit rows: the audit entity itself is never selected.
        foreach (var entry in AuditedChanges(db).ToArray())
        {
            var audit = await CreateAuditAsync(db, entry, cancellationToken);
            if (audit is not null) db.Set<AuditLog>().Add(audit);
        }
        return result;
    }

    private async Task<AuditLog?> CreateAuditAsync(DbContext db, EntityEntry entry, CancellationToken cancellationToken)
    {
        var entityType = entry.Metadata;
        var schema = entityType.GetSchema();
        var table = entityType.GetTableName();
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException("Audited entity requires a schema-qualified table.");
        var stableType = $"{schema}.{table}";
        var (entityId, entityKey) = Identity(entry);
        var settingKey = entry.Entity is Setting setting ? setting.Key : null;
        if (entry.State is EntityState.Modified or EntityState.Deleted)
            await LockOriginalRowAsync(db, entry, schema, table, cancellationToken);
        var dbValues = entry.State is EntityState.Modified or EntityState.Deleted
            ? await entry.GetDatabaseValuesAsync(cancellationToken)
            : null;
        if (entry.State is EntityState.Modified or EntityState.Deleted && dbValues is null)
            throw new InvalidOperationException("Audited entity original values are unavailable.");

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey()) continue;
            // Before INSERT, a database-generated value (for example settings.updated_at
            // DEFAULT now()) is not known yet. Never write the CLR sentinel as the after value.
            if (entry.State == EntityState.Added && property.Metadata.ValueGenerated != ValueGenerated.Never) continue;
            if (entry.State == EntityState.Modified && property.Metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate) continue;
            if (entry.State == EntityState.Modified && !property.IsModified) continue;
            var before = dbValues?[property.Metadata.Name];
            var after = entry.State == EntityState.Deleted ? null : property.CurrentValue;
            if (entry.State == EntityState.Modified && Equals(before, after)) continue;

            var field = property.Metadata.GetColumnName();
            if (string.IsNullOrWhiteSpace(field))
                throw new InvalidOperationException("Audited property requires a database column name.");
            fields[field] = new
            {
                before = entry.State == EntityState.Added ? null : AuditValuePolicy.Filter(stableType, settingKey, property.Metadata.Name, before),
                after = entry.State == EntityState.Deleted ? null : AuditValuePolicy.Filter(stableType, settingKey, property.Metadata.Name, after)
            };
        }
        if (fields.Count == 0 && entry.State == EntityState.Modified) return null;

        var actorType = auditContext.ActorType;
        var actorId = auditContext.ActorId;
        if (actorType is not ("staff" or "customer" or "system")
            || (actorType == "system" && actorId is not null)
            || (actorType != "system" && actorId is null))
            throw new InvalidOperationException("Audit actor context is invalid.");

        var correlation = auditContext.CorrelationId;
        if (correlation is not null && !Guid.TryParseExact(correlation, "N", out _)
            && !Guid.TryParseExact(correlation, "D", out _))
            correlation = null;
        return new AuditLog
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.UtcNow.UtcDateTime,
            ActorType = actorType,
            ActorId = actorId,
            EntityType = stableType,
            EntityId = entityId,
            EntityKey = entityKey,
            Action = entry.State switch
            {
                EntityState.Added => "create",
                EntityState.Modified => "update",
                EntityState.Deleted => "delete",
                _ => throw new InvalidOperationException("Unsupported audited entity state.")
            },
            Changes = JsonSerializer.Serialize(fields),
            CorrelationId = correlation
        };
    }

    private static (Guid? EntityId, string? EntityKey) Identity(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key?.Properties.Count != 1)
            throw new InvalidOperationException("Audited entity requires an approved single-column key.");
        var value = entry.Property(key.Properties[0].Name).CurrentValue;
        return value switch
        {
            Guid id when id != Guid.Empty => (id, null),
            string text when !string.IsNullOrEmpty(text) => (null, text),
            _ => throw new InvalidOperationException("Audited entity key is empty or unsupported.")
        };
    }

    private static async Task LockOriginalRowAsync(DbContext db, EntityEntry entry, string schema, string table,
        CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Audited updates require an active transaction.");
        var key = entry.Metadata.FindPrimaryKey()!.Properties[0];
        var keyColumn = key.GetColumnName() ?? throw new InvalidOperationException("Audited key requires a database column.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = $"SELECT 1 FROM {Quote(schema)}.{Quote(table)} WHERE {Quote(keyColumn)} = @audit_key FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "audit_key";
        parameter.Value = entry.Property(key.Name).CurrentValue!;
        command.Parameters.Add(parameter);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("Audited entity original row is unavailable.");
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static IEnumerable<EntityEntry> AuditedChanges(DbContext db) => db.ChangeTracker.Entries()
        .Where(entry => entry.Metadata.ClrType.IsDefined(typeof(AuditedAttribute), inherit: false)
            && entry.Entity is not AuditLog
            && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
}
