namespace Sellora.InventoryService.Domain.Inventory;

public enum StockMovementType
{
    ManualAdjustment = 1,
    Reserved = 2,
    Released = 3,
    Sold = 4,
    Returned = 5
}