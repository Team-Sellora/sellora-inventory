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
    DateTimeOffset DetectedAt,
    // US-E5-4 (additive, schema stays 1.0): who holds the stock, so the
    // Notification service can tell the owning agency without calling back.
    string? OwnerType = null,
    Guid? ExternalOwnerId = null,
    string? OwnerDisplayName = null);
