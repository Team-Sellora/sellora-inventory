namespace Sellora.InventoryService.Application.Stock;

public sealed record UpdateReorderThresholdRequest(Guid StockItemId, int? ReorderThreshold);
