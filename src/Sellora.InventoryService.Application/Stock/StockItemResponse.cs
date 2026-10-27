namespace Sellora.InventoryService.Application.Stock;

public sealed record StockItemResponse(
    Guid StockItemId,
    Guid InventoryOwnerId,
    string OwnerType,
    Guid ExternalOwnerId,
    string OwnerDisplayName,
    Guid ProductId,
    Guid? BatchId,
    int QuantityOnHand,
    int QuantityReserved,
    int AvailableQuantity,
    int? ReorderThreshold,
    DateTimeOffset UpdatedAt);