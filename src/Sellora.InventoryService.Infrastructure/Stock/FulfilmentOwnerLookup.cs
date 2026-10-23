using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class FulfilmentOwnerLookup : IFulfilmentOwnerLookup
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;

    public FulfilmentOwnerLookup(
        InventoryDbContext db,
        ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    public Task<Guid?> FindAgencyOwnerAsync(
        Guid agencyId,
        CancellationToken cancellationToken = default) =>
        FindOwnerAsync(
            InventoryOwnerType.Agency,
            agencyId,
            cancellationToken);

    public Task<Guid?> FindCompanyOwnerAsync(
        CancellationToken cancellationToken = default) =>
        FindOwnerAsync(
            InventoryOwnerType.Company,
            _tenantContext.CompanyId ?? Guid.Empty,
            cancellationToken);

    private async Task<Guid?> FindOwnerAsync(
        InventoryOwnerType ownerType,
        Guid externalOwnerId,
        CancellationToken cancellationToken)
    {
        if (_tenantContext.CompanyId is not Guid companyId ||
            companyId == Guid.Empty ||
            externalOwnerId == Guid.Empty)
        {
            return null;
        }

        return await _db.InventoryOwners
            .AsNoTracking()
            .Where(owner =>
                owner.CompanyId == companyId &&
                owner.OwnerType == ownerType &&
                owner.ExternalOwnerId == externalOwnerId &&
                owner.IsActive)
            .Select(owner => (Guid?)owner.InventoryOwnerId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}