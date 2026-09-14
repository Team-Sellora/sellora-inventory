namespace Sellora.InventoryService.Application.Stock;

public interface IFulfilmentOwnerLookup
{
    Task<Guid?> FindAgencyOwnerAsync(
        Guid agencyId,
        CancellationToken cancellationToken = default);

    Task<Guid?> FindCompanyOwnerAsync(
        CancellationToken cancellationToken = default);
}