using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Domain.Entities;

public class InventoryOwner : ITenantScoped
{
    public Guid InventoryOwnerId { get; set; }

    public Guid CompanyId { get; set; }

    public InventoryOwnerType OwnerType { get; set; }

    // Company, agency, or sales-rep ID from the Organization service.
    public Guid ExternalOwnerId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<StockItem> StockItems { get; set; } = new List<StockItem>();
}