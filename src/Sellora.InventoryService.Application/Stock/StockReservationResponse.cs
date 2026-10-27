namespace Sellora.InventoryService.Application.Stock;

public sealed record StockReservationResponse(
    Guid ReservationId,
    string OrderReference,
    Guid InventoryOwnerId,
    string Status,
    DateTimeOffset ExpiresAt,
    IReadOnlyCollection<ReservationLineResponse> Lines);