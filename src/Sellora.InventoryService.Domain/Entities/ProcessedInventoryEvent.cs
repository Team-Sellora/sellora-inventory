using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Domain.Entities;

public sealed class ProcessedInventoryEvent : ITenantScoped
{
    public Guid CompanyId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
}
