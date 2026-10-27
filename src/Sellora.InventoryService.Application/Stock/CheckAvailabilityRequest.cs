namespace Sellora.InventoryService.Application.Stock;

public sealed record CheckAvailabilityRequest(
    Guid InventoryOwnerId,
    IReadOnlyCollection<ReservationLineRequest> Lines);