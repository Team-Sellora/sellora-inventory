namespace Sellora.InventoryService.Application.Stock;

public sealed record ReservationLineResponse(
    Guid StockItemId,
    Guid ProductId,
    Guid? BatchId,
    int Quantity);