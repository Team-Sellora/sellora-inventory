namespace Sellora.InventoryService.Application.Stock;

public sealed record ReservationLineRequest(
    Guid ProductId,
    Guid? BatchId,
    int Quantity);