using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configurations;

public sealed class StockMovementConfiguration
    : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable(
            "stock_movement",
            table => table.HasCheckConstraint(
                "ck_stock_movement_type",
                "movement_type IN (" +
                "'ManualAdjustment', 'Reserved', 'Released', 'Sold', 'Returned')"));

        builder.HasKey(movement => movement.StockMovementId)
            .HasName("pk_stock_movement");

        builder.Property(movement => movement.StockMovementId)
            .HasColumnName("stock_movement_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(movement => movement.StockItemId)
            .HasColumnName("stock_item_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(movement => movement.ReservationId)
            .HasColumnName("reservation_id")
            .HasColumnType("uuid");

        builder.Property(movement => movement.MovementType)
            .HasColumnName("movement_type")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(movement => movement.OnHandDelta)
            .HasColumnName("on_hand_delta")
            .IsRequired();

        builder.Property(movement => movement.ReservedDelta)
            .HasColumnName("reserved_delta")
            .IsRequired();

        builder.Property(movement => movement.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(255);

        builder.Property(movement => movement.ReferenceType)
            .HasColumnName("reference_type")
            .HasMaxLength(40);

        builder.Property(movement => movement.ReferenceId)
            .HasColumnName("reference_id")
            .HasMaxLength(100);

        builder.Property(movement => movement.Reason)
            .HasColumnName("reason")
            .HasColumnType("text");

        builder.Property(movement => movement.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne(movement => movement.StockItem)
            .WithMany(stockItem => stockItem.Movements)
            .HasForeignKey(movement => movement.StockItemId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_movement_stock_item");

        builder.HasIndex(movement => new
        {
            movement.StockItemId,
            movement.OccurredAt
        })
        .HasDatabaseName("ix_stock_movement_stock_item_occurred_at");
    }
}