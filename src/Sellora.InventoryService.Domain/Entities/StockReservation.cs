using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Domain.Entities;

public sealed class StockReservation : ITenantScoped
{
    public Guid ReservationId { get; set; }

    public Guid CompanyId { get; set; }

    // Idempotency key supplied by the Order service.
    public string OrderReference { get; set; } = string.Empty;

    // The single owner whose stock is held by this reservation.
    public Guid InventoryOwnerId { get; set; }

    public ReservationStatus Status { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<StockReservationLine> Lines { get; set; } =
        new List<StockReservationLine>();
}