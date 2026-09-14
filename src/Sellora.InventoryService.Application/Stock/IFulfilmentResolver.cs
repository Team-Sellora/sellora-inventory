namespace Sellora.InventoryService.Application.Stock;

public interface IFulfilmentResolver
{
    Task<ReserveStockResult> ResolveFulfilmentSourceAsync(
        ResolveFulfilmentRequest request,
        CancellationToken cancellationToken = default);
}