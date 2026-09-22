namespace Sellora.InventoryService.Application.Events;

public sealed record LowStockDetectedEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid StockItemId,
    Guid InventoryOwnerId,
    Guid ProductId,
    Guid? BatchId,
    int AvailableQuantity,
    int ReorderThreshold,
    DateTimeOffset DetectedAt);
