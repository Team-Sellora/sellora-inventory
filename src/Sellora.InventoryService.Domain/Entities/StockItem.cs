using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Domain.Exceptions;

namespace Sellora.InventoryService.Domain.Entities;

public class StockItem : ITenantScoped
{
    public Guid StockItemId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid InventoryOwnerId { get; set; }

    // Product belongs to the Product & Catalog service.
    public Guid ProductId { get; set; }

    public Guid? BatchId { get; set; }

    public int QuantityOnHand { get; private set; }

    public int QuantityReserved { get; private set; }

    public int AvailableQuantity => QuantityOnHand - QuantityReserved;

    public int? ReorderThreshold { get; set; }

    public bool LowStockNotified { get; set; }

    public long RowVersion { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public InventoryOwner InventoryOwner { get; set; } = null!;

    public ICollection<StockMovement> Movements { get; set; } =
        new List<StockMovement>();

    public void ApplyMovement(StockMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        if (movement.StockMovementId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A stock movement must have an identifier.");
        }

        if (movement.StockItemId != StockItemId)
        {
            throw new InvalidOperationException(
                "The stock movement belongs to a different stock item.");
        }

        if (Movements.Any(existing =>
            existing.StockMovementId == movement.StockMovementId))
        {
            throw new InvalidOperationException(
                "The stock movement has already been applied.");
        }

        var nextOnHand = QuantityOnHand + movement.OnHandDelta;
        var nextReserved = QuantityReserved + movement.ReservedDelta;

        if (nextOnHand < 0)
        {
            throw new InsufficientStockException();
        }

        if (nextReserved < 0)
        {
            throw new InvalidOperationException(
                "Reserved stock quantity cannot be negative.");
        }

        if (nextReserved > nextOnHand)
        {
            throw new InvalidOperationException(
                "Reserved stock quantity cannot exceed on-hand quantity.");
        }

        QuantityOnHand = nextOnHand;
        QuantityReserved = nextReserved;
        RowVersion++;
        UpdatedAt = movement.OccurredAt;

        Movements.Add(movement);
    }
}
