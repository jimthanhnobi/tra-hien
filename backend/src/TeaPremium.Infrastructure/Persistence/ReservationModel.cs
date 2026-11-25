using Microsoft.EntityFrameworkCore;

namespace TeaPremium.Infrastructure.Persistence;

internal static class ReservationModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.Entity<Reservation>(b =>
        {
            b.ToTable("reservations", "inventory", t =>
            {
                t.HasCheckConstraint("ck_reservations_quantity", "quantity > 0");
                t.HasCheckConstraint("ck_reservations_status", "status IN ('active','consumed','released')");
                t.HasCheckConstraint("ck_reservations_active_closed", "(status = 'active') = (closed_at IS NULL)");
                t.HasCheckConstraint("ck_reservations_order_line_required", "status = 'released' OR order_line_id IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_reservations");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.OrderLineId).HasColumnName("order_line_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'active'");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.CloseReason).HasColumnName("close_reason").HasColumnType("text").IsRequired(false);

            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId)
                .HasConstraintName("fk_reservations_variant_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<OrderLine>().WithMany().HasForeignKey(x => x.OrderLineId)
                .HasConstraintName("fk_reservations_line").OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(x => x.OrderLineId).IsUnique().HasDatabaseName("ux_reservation_active_line")
                .HasFilter("status = 'active'");
            b.HasIndex(x => x.VariantId).HasDatabaseName("ix_reservation_variant_active")
                .HasFilter("status = 'active'");
        });
    }
}
