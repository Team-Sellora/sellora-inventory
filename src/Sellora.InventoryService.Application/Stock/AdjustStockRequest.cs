namespace Sellora.InventoryService.Application.Stock;

public sealed record AdjustStockRequest(
    Guid InventoryOwnerId,
    Guid ProductId,
    Guid? BatchId,
    int QuantityDelta,
    string Reason);