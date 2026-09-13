using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configurations;

public sealed class StockItemConfiguration
    : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable(
            "stock_item",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_stock_item_on_hand_non_negative",
                    "quantity_on_hand >= 0");

                table.HasCheckConstraint(
                    "ck_stock_item_reserved_non_negative",
                    "quantity_reserved >= 0");

                table.HasCheckConstraint(
                    "ck_stock_item_reserved_not_above_on_hand",
                    "quantity_reserved <= quantity_on_hand");
            });

        builder.HasKey(stockItem => stockItem.StockItemId)
            .HasName("pk_stock_item");

        builder.Property(stockItem => stockItem.StockItemId)
            .HasColumnName("stock_item_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(stockItem => stockItem.CompanyId)
            .HasColumnName("company_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(stockItem => stockItem.InventoryOwnerId)
            .HasColumnName("inventory_owner_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(stockItem => stockItem.ProductId)
            .HasColumnName("product_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(stockItem => stockItem.BatchId)
            .HasColumnName("batch_id")
            .HasColumnType("uuid");

        builder.Property(stockItem => stockItem.QuantityOnHand)
            .HasColumnName("quantity_on_hand")
            .IsRequired();

        builder.Property(stockItem => stockItem.QuantityReserved)
            .HasColumnName("quantity_reserved")
            .IsRequired();

        builder.Ignore(stockItem => stockItem.AvailableQuantity);

        builder.Property(stockItem => stockItem.ReorderThreshold)
            .HasColumnName("reorder_threshold");

        builder.Property(stockItem => stockItem.LowStockNotified)
            .HasColumnName("low_stock_notified")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(stockItem => stockItem.RowVersion)
            .HasColumnName("row_version")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(stockItem => stockItem.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne(stockItem => stockItem.InventoryOwner)
            .WithMany(owner => owner.StockItems)
            .HasForeignKey(stockItem => stockItem.InventoryOwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_item_inventory_owner");

        // PostgreSQL permits several NULL values in a normal unique index.
        // These two partial unique indexes preserve uniqueness for both
        // batch-tracked and non-batch stock.
        builder.HasIndex(stockItem => new
        {
            stockItem.InventoryOwnerId,
            stockItem.ProductId,
            stockItem.BatchId
        })
        .IsUnique()
        .HasFilter("\"batch_id\" IS NOT NULL")
        .HasDatabaseName("uq_stock_item_owner_product_batch");

        builder.HasIndex(stockItem => new
        {
            stockItem.InventoryOwnerId,
            stockItem.ProductId
        })
        .IsUnique()
        .HasFilter("\"batch_id\" IS NULL")
        .HasDatabaseName("uq_stock_item_owner_product_without_batch");
    }
}