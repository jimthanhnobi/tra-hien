using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class UuidAuditTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Fact]
    public async Task BusinessUuidKeyIsRecordedWithoutTextCoercion()
    {
        await fixture.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS audit_test; CREATE TABLE IF NOT EXISTS audit_test.customers (id uuid PRIMARY KEY, phone text NOT NULL, status text NOT NULL, amount_vnd bigint NOT NULL)");
        var id = Guid.CreateVersion7();
        await using (var db = CreateContext())
        {
            db.Customers.Add(new AuditedCustomer { Id = id, Phone = "0901234567", Status = "active", AmountVnd = 30000 });
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateContext();
        var row = Assert.Single(await reader.AuditLogs.Where(x => x.EntityType == "audit_test.customers" && x.EntityId == id).ToListAsync());
        Assert.Null(row.EntityKey);
        Assert.Equal("create", row.Action);
        Assert.Contains("30000", row.Changes, StringComparison.Ordinal);
        Assert.Contains("active", row.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("0901234567", row.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeKeyIsRejectedWithoutInventingAnEncoding()
    {
        await using var db = CreateContext();
        db.Preferences.Add(new AuditedCustomerPreference { CustomerId = Guid.CreateVersion7(), Code = "tea" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Audited entity requires an approved single-column key.", error.Message);
    }

    [Fact]
    public async Task EmptyUuidKeyIsRejected()
    {
        await using var db = CreateContext();
        db.Customers.Add(new AuditedCustomer { Id = Guid.Empty, Phone = "private", Status = "active" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Audited entity key is empty or unsupported.", error.Message);
    }

    [Fact]
    public async Task AuditedCreateWithOnlyAKeyStillProducesAnAuditRow()
    {
        await fixture.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS audit_test; CREATE TABLE IF NOT EXISTS audit_test.customer_pointers (id uuid PRIMARY KEY)");
        var id = Guid.CreateVersion7();
        await using (var db = CreateContext())
        {
            db.CustomerPointers.Add(new AuditedCustomerPointer { Id = id });
            await db.SaveChangesAsync();
        }
        await using var reader = fixture.CreateContext();
        var audit = Assert.Single(await reader.AuditLogs.Where(x => x.EntityType == "audit_test.customer_pointers" && x.EntityId == id).ToListAsync());
        Assert.Equal("create", audit.Action);
        Assert.Equal("{}", audit.Changes);
    }

    private AuditTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedClock(), new SystemAuditContext()))
            .Options;
        return new AuditTestDbContext(options);
    }

    [Audited]
    private sealed class AuditedCustomer
    {
        public Guid Id { get; set; }
        public string Phone { get; set; } = "";
        public string Status { get; set; } = "";
        public long AmountVnd { get; set; }
    }

    [Audited]
    private sealed class AuditedCustomerPreference
    {
        public Guid CustomerId { get; set; }
        public string Code { get; set; } = "";
    }

    [Audited]
    private sealed class AuditedCustomerPointer
    {
        public Guid Id { get; set; }
    }

    private sealed class AuditTestDbContext(DbContextOptions<AuditTestDbContext> options) : DbContext(options)
    {
        public DbSet<AuditedCustomer> Customers => Set<AuditedCustomer>();
        public DbSet<AuditedCustomerPreference> Preferences => Set<AuditedCustomerPreference>();
        public DbSet<AuditedCustomerPointer> CustomerPointers => Set<AuditedCustomerPointer>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AuditedCustomer>(b =>
            {
                b.ToTable("customers", "audit_test");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever().HasColumnName("id");
                b.Property(x => x.Phone).HasColumnName("phone");
                b.Property(x => x.Status).HasColumnName("status");
                b.Property(x => x.AmountVnd).HasColumnName("amount_vnd");
            });
            modelBuilder.Entity<AuditedCustomerPreference>(b =>
            {
                b.ToTable("customer_preferences", "audit_test");
                b.HasKey(x => new { x.CustomerId, x.Code });
                b.Property(x => x.CustomerId).HasColumnName("customer_id");
                b.Property(x => x.Code).HasColumnName("code");
            });
            modelBuilder.Entity<AuditedCustomerPointer>(b =>
            {
                b.ToTable("customer_pointers", "audit_test");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever().HasColumnName("id");
            });
            modelBuilder.Entity<AuditLog>(b =>
            {
                b.ToTable("audit_logs", "platform");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever().HasColumnName("id");
                b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
                b.Property(x => x.ActorType).HasColumnName("actor_type");
                b.Property(x => x.ActorId).HasColumnName("actor_id");
                b.Property(x => x.EntityType).HasColumnName("entity_type");
                b.Property(x => x.EntityId).HasColumnName("entity_id");
                b.Property(x => x.EntityKey).HasColumnName("entity_key");
                b.Property(x => x.Action).HasColumnName("action");
                b.Property(x => x.Changes).HasColumnName("changes").HasColumnType("jsonb");
                b.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            });
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    }
}
