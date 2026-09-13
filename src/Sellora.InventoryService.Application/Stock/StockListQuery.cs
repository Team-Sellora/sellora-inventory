namespace Sellora.InventoryService.Application.Stock;

public sealed record StockListQuery(
    Guid? ProductId = null,
    Guid? InventoryOwnerId = null);