namespace Sellora.InventoryService.Application.Stock;

public sealed record ReservationShortage(
    Guid ProductId,
    Guid? BatchId,
    int RequestedQuantity,
    int AvailableQuantity);