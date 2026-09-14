namespace Sellora.InventoryService.Application.Stock;

public sealed record StockAvailability(
    Guid ProductId,
    Guid? BatchId,
    int RequestedQuantity,
    int AvailableQuantity,
    bool IsAvailable);