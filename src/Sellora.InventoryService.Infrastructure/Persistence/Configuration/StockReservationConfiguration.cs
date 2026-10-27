using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configurations;

public sealed class StockReservationConfiguration
    : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable("stock_reservation");

        builder.HasKey(reservation => reservation.ReservationId)
            .HasName("pk_stock_reservation");

        builder.Property(reservation => reservation.ReservationId)
            .HasColumnName("reservation_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(reservation => reservation.CompanyId)
            .HasColumnName("company_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(reservation => reservation.OrderReference)
            .HasColumnName("order_reference")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(reservation => reservation.InventoryOwnerId)
            .HasColumnName("inventory_owner_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(reservation => reservation.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(reservation => reservation.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(reservation => reservation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(reservation => reservation.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(reservation => new
        { reservation.CompanyId, reservation.OrderReference })
            .IsUnique()
            .HasDatabaseName(
                "uq_stock_reservation_company_order_reference");

        builder.HasIndex(reservation => new
        { reservation.Status, reservation.ExpiresAt })
            .HasDatabaseName("ix_stock_reservation_status_expires_at");

        builder.HasOne<InventoryOwner>()
            .WithMany()
            .HasForeignKey(reservation => reservation.InventoryOwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_stock_reservation_inventory_owner");

        builder.HasMany(reservation => reservation.Lines)
            .WithOne(line => line.Reservation)
            .HasForeignKey(line => line.ReservationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StockReservationLineConfiguration
    : IEntityTypeConfiguration<StockReservationLine>
{
    public void Configure(EntityTypeBuilder<StockReservationLine> builder)
    {
        builder.ToTable(
            "stock_reservation_line",
            table => table.HasCheckConstraint(
                "ck_stock_reservation_line_quantity_positive",
                "quantity > 0"));

        builder.HasKey(line => line.StockReservationLineId)
            .HasName("pk_stock_reservation_line");

        builder.Property(line => line.StockReservationLineId)
            .HasColumnName("stock_reservation_line_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(line => line.ReservationId)
            .HasColumnName("reservation_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(line => line.StockItemId)
            .HasColumnName("stock_item_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(line => line.ProductId)
            .HasColumnName("product_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(line => line.BatchId)
            .HasColumnName("batch_id")
            .HasColumnType("uuid");

        builder.Property(line => line.Quantity)
            .HasColumnName("quantity")
            .IsRequired();

        builder.HasIndex(line => new
        { line.ReservationId, line.StockItemId })
            .IsUnique()
            .HasDatabaseName(
                "uq_stock_reservation_line_reservation_stock_item");

        builder.HasOne<StockItem>()
            .WithMany()
            .HasForeignKey(line => line.StockItemId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_stock_reservation_line_stock_item");
    }
}