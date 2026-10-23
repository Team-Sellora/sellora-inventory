using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configurations;

public sealed class InventoryOwnerConfiguration
    : IEntityTypeConfiguration<InventoryOwner>
{
    public void Configure(EntityTypeBuilder<InventoryOwner> builder)
    {
        builder.ToTable(
            "inventory_owner",
            table => table.HasCheckConstraint(
                "ck_inventory_owner_type",
                "owner_type IN ('Company', 'Agency', 'SalesRep')"));

        builder.HasKey(owner => owner.InventoryOwnerId)
            .HasName("pk_inventory_owner");

        builder.Property(owner => owner.InventoryOwnerId)
            .HasColumnName("inventory_owner_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(owner => owner.CompanyId)
            .HasColumnName("company_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(owner => owner.OwnerType)
            .HasColumnName("owner_type")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(owner => owner.ExternalOwnerId)
            .HasColumnName("external_owner_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(owner => owner.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(owner => owner.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(owner => owner.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(owner => new
        {
            owner.CompanyId,
            owner.OwnerType,
            owner.ExternalOwnerId
        })
        .IsUnique()
        .HasDatabaseName("uq_inventory_owner_company_type_external_owner");
    }
}