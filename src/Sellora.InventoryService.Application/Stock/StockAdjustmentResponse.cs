namespace Sellora.InventoryService.Application.Stock;

public sealed record StockAdjustmentResponse(
    Guid StockItemId,
    Guid StockMovementId,
    Guid InventoryOwnerId,
    Guid ProductId,
    Guid? BatchId,
    int QuantityOnHand,
    int QuantityReserved,
    int AvailableQuantity,
    int QuantityDelta,
    string Reason,
    DateTimeOffset OccurredAt);