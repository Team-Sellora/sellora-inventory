using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.InventoryService.Domain.Entities;

namespace Sellora.InventoryService.Infrastructure.Persistence.Configuration;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_message");
        builder.HasKey(x => x.OutboxId).HasName("pk_outbox_message");
        builder.Property(x => x.OutboxId).HasColumnName("outbox_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.CompanyId).HasColumnName("company_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(120).IsRequired();
        builder.Property(x => x.SchemaVersion).HasColumnName("schema_version").HasMaxLength(16).IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(x => new { x.PublishedAt, x.NextAttemptAt }).HasDatabaseName("ix_outbox_message_pending_relay");
    }
}
