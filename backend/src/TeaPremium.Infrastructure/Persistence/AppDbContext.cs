using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using TeaPremium.Infrastructure.Iam;

namespace TeaPremium.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<StaffUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All DDL indexes (including IAM/platform) are explicitly configured.
        configurationBuilder.Conventions.Remove<Microsoft.EntityFrameworkCore.Metadata.Conventions.ForeignKeyIndexConvention>();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsIdGenerator, RepositoryMigrationIds>();

    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Province> Provinces => Set<Province>();
    public DbSet<Ward> Wards => Set<Ward>();
    public DbSet<TeaPremium.Infrastructure.AdministrativeData.AdministrativeDataImport> AdministrativeDataImports => Set<TeaPremium.Infrastructure.AdministrativeData.AdministrativeDataImport>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<InboundWebhook> InboundWebhooks => Set<InboundWebhook>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        IamModel.Configure(builder);
        CatalogInventoryModel.Configure(builder);
        TeaPremium.Infrastructure.AdministrativeData.AdministrativeImportModel.Configure(builder);
        builder.HasPostgresExtension("unaccent");
        builder.HasPostgresExtension("pg_trgm");
        builder.HasPostgresExtension("btree_gist");

        builder.Entity<Setting>(b =>
        {
            b.ToTable("settings", "platform", t => t.HasCheckConstraint("ck_settings_key_format", "key ~ '^[a-z0-9_.]{3,80}$'"));
            b.HasKey(x => x.Key).HasName("pk_settings");
            b.Property(x => x.Value).HasColumnType("jsonb");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });
        builder.Entity<Province>(b =>
        {
            b.ToTable("provinces", "platform");
            b.HasKey(x => x.Code).HasName("pk_provinces");
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.Property(x => x.SortOrder).HasDefaultValue(0);
        });
        builder.Entity<Ward>(b =>
        {
            b.ToTable("wards", "platform");
            b.HasKey(x => x.Code).HasName("pk_wards");
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.HasOne(x => x.Province).WithMany(x => x.Wards).HasForeignKey(x => x.ProvinceCode)
                .HasConstraintName("fk_wards_provinces_province_code").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.ProvinceCode).HasDatabaseName("ix_wards_province");
        });
        builder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs", "platform", t =>
            {
                t.HasCheckConstraint("ck_audit_logs_actor_type", "actor_type IN ('staff','customer','system')");
                t.HasCheckConstraint("ck_audit_logs_action", "action IN ('create','update','delete','state_change','access')");
                t.HasCheckConstraint("ck_audit_entity_identity", "num_nonnulls(entity_id, entity_key) = 1 AND (entity_key IS NULL OR entity_key <> '')");
            });
            b.HasKey(x => x.Id).HasName("pk_audit_logs");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Changes).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAt }).HasDatabaseName("ix_audit_entity_id")
                .IsDescending(false, false, true).HasFilter("entity_id IS NOT NULL");
            b.HasIndex(x => new { x.EntityType, x.EntityKey, x.OccurredAt }).HasDatabaseName("ix_audit_entity_key")
                .IsDescending(false, false, true).HasFilter("entity_key IS NOT NULL");
            b.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_audit_time").IsDescending();
            b.HasIndex(x => new { x.ActorId, x.OccurredAt }).HasDatabaseName("ix_audit_actor")
                .IsDescending(false, true).HasFilter("actor_id IS NOT NULL");
        });
        builder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "platform", t => t.HasCheckConstraint("ck_outbox_messages_status", "status IN ('pending','processed','dead')"));
            b.HasKey(x => x.Id).HasName("pk_outbox_messages");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.Attempts).HasDefaultValue(0);
            b.Property(x => x.Status).HasDefaultValue("pending");
            b.HasIndex(x => x.AvailableAt).HasDatabaseName("ix_outbox_pending").HasFilter("status = 'pending'");
        });
        builder.Entity<IdempotencyKey>(b =>
        {
            b.ToTable("idempotency_keys", "platform");
            b.HasKey(x => new { x.Scope, x.Key }).HasName("pk_idempotency_keys");
            b.Property(x => x.ResponseBody).HasColumnType("jsonb");
            // Cleanup must not delete a fresh replacement selected under an older snapshot.
            // EF adds created_at to DELETE predicates; this changes no PostgreSQL DDL.
            b.Property(x => x.CreatedAt).IsConcurrencyToken().HasDefaultValueSql("now()");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_idem_created");
        });
        builder.Entity<InboundWebhook>(b =>
        {
            b.ToTable("inbound_webhooks", "platform", t => t.HasCheckConstraint("ck_inbound_webhooks_status", "status IN ('received','processed','ignored','failed')"));
            b.HasKey(x => x.Id).HasName("pk_inbound_webhooks");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasAlternateKey(x => new { x.Provider, x.ExternalId }).HasName("ux_inbound_webhooks_provider_external_id");
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.ReceivedAt).HasDefaultValueSql("now()");
            b.Property(x => x.Status).HasDefaultValue("received");
        });
        builder.Entity<NotificationLog>(b =>
        {
            b.ToTable("notification_logs", "platform", t =>
            {
                t.HasCheckConstraint("ck_notification_logs_channel", "channel IN ('email','telegram','sms','zns')");
                t.HasCheckConstraint("ck_notification_logs_status", "status IN ('queued','sent','failed')");
            });
            b.HasKey(x => x.Id).HasName("pk_notification_logs");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.RefType, x.RefId }).HasDatabaseName("ix_notif_ref");
        });
        builder.Entity<DataProtectionKey>(b =>
        {
            b.ToTable("data_protection_keys", "platform");
            b.HasKey(x => x.Id).HasName("pk_data_protection_keys");
            b.Property(x => x.Id).UseSerialColumn();
        });

        // The target DDL uses snake_case and concrete PostgreSQL types throughout.
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.FindAnnotation("Relational:ColumnName") is null)
                    property.SetColumnName(ToSnakeCase(property.Name));
                if (property.GetColumnType() is not null) continue;
                property.SetColumnType(property.ClrType switch
                {
                    var t when t == typeof(string) => "text",
                    var t when t == typeof(Guid) || t == typeof(Guid?) => "uuid",
                    var t when t == typeof(DateTime) || t == typeof(DateTime?) => "timestamp with time zone",
                    var t when t == typeof(DateTimeOffset) || t == typeof(DateTimeOffset?) => "timestamp with time zone",
                    var t when t == typeof(int) || t == typeof(int?) => "integer",
                    var t when t == typeof(long) || t == typeof(long?) => "bigint",
                    var t when t == typeof(bool) || t == typeof(bool?) => "boolean",
                    _ => throw new InvalidOperationException($"Unmapped PostgreSQL type: {entity.Name}.{property.Name}")
                });
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var chars = new System.Text.StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (char.IsUpper(c) && chars.Length > 0) chars.Append('_');
            chars.Append(char.ToLowerInvariant(c));
        }
        return chars.ToString();
    }
}
