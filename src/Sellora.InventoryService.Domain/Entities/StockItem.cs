using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Domain.Entities;

public class StockItem : ITenantScoped
{
    public Guid StockItemId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid InventoryOwnerId { get; set; }

    // Product belongs to the Product & Catalog service.
    public Guid ProductId { get; set; }

    // Nullable because batch-level stock may be introduced per product.
    public Guid? BatchId { get; set; }

    public int QuantityOnHand { get; set; }

    public int QuantityReserved { get; set; }

    public int AvailableQuantity => QuantityOnHand - QuantityReserved;

    public int? ReorderThreshold { get; set; }

    public bool LowStockNotified { get; set; }

    // Used later for optimistic concurrency protection.
    public long RowVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public InventoryOwner InventoryOwner { get; set; } = null!;

    public ICollection<StockMovement> Movements { get; set; } =
        new List<StockMovement>();
}