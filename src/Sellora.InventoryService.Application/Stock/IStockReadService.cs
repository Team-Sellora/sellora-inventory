namespace Sellora.InventoryService.Application.Stock;

public interface IStockReadService
{
    Task<IReadOnlyCollection<StockItemResponse>> GetStockAsync(
        StockListQuery query,
        CancellationToken cancellationToken = default);
}