using Sellora.InventoryService.Domain.Inventory;

namespace Sellora.InventoryService.Domain.Entities;

public class StockMovement
{
    public Guid StockMovementId { get; set; }

    public Guid StockItemId { get; set; }

    // Will be used by reservation/order stories. It is intentionally nullable
    // because manual stock adjustments do not have a reservation.
    public Guid? ReservationId { get; set; }

    public StockMovementType MovementType { get; set; }

    public int OnHandDelta { get; set; }

    public int ReservedDelta { get; set; }

    public string? ActorId { get; set; }

    public string? ReferenceType { get; set; }

    public string? ReferenceId { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public StockItem StockItem { get; set; } = null!;
}