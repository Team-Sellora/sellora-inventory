using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configurations;

public sealed class ProcessedInventoryEventConfiguration
    : IEntityTypeConfiguration<ProcessedInventoryEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedInventoryEvent> builder)
    {
        builder.ToTable("processed_inventory_event");
        builder.HasKey(e => new { e.CompanyId, e.EventId });
        builder.Property(e => e.CompanyId).HasColumnName("company_id");
        builder.Property(e => e.EventId).HasColumnName("event_id");
        builder.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(80).IsRequired();
        builder.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.ProcessedAt).HasColumnName("processed_at");
    }
}
