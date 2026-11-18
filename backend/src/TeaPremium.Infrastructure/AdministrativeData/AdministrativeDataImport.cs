using Microsoft.EntityFrameworkCore;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.AdministrativeData;

public sealed class AdministrativeDataImport
{
    public Guid Id { get; set; }
    public Guid? PreviousImportId { get; set; }
    public string DatasetVersion { get; set; } = "";
    public long ReleaseOrder { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public string Operation { get; set; } = "";
    public string Manifest { get; set; } = "{}";
    public string ManifestSha256 { get; set; } = "";
    public string CsvSha256 { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string CatalogSha256 { get; set; } = "";
    public string Receipt { get; set; } = "{}";
    public DateTime AppliedAt { get; set; }
    public Guid? RecoveryOf { get; set; }
    public Guid? ReAdvanceOf { get; set; }
}

public static class AdministrativeImportModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AdministrativeDataImport>(b =>
        {
            // SQL-owned DDL: EF alternate keys cannot represent this nullable predecessor
            // UNIQUE NULLS NOT DISTINCT constraint. The migration uses the approved SQL,
            // and real PostgreSQL catalog parity tests verify its constraints and grants.
            b.ToTable("administrative_data_imports", "platform", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id).HasName("pk_administrative_data_imports");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Manifest).HasColumnType("jsonb");
            b.Property(x => x.Receipt).HasColumnType("jsonb");
            b.Property(x => x.ReleaseOrder).HasColumnType("bigint");
            b.Property(x => x.EffectiveOn).HasColumnType("date");
            // Cross-row foreign keys and the nullable unique predecessor are SQL-owned too;
            // no implicit EF indexes are introduced beyond the approved PK/UNIQUE indexes.
        });
    }
}
