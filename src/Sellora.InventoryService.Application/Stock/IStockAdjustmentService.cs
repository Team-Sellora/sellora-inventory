namespace Sellora.InventoryService.Application.Stock;

public interface IStockAdjustmentService
{
    Task<AdjustStockResult> AdjustAsync(
        AdjustStockRequest request,
        CancellationToken cancellationToken = default);
}