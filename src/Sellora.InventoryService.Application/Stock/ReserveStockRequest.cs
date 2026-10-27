namespace Sellora.InventoryService.Application.Stock;

public sealed record ReserveStockRequest(
    string OrderReference,
    Guid InventoryOwnerId,
    IReadOnlyCollection<ReservationLineRequest> Lines);