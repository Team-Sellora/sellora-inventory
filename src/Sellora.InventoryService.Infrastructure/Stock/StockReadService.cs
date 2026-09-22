using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class StockReadService : IStockReadService
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserContext _currentUser;

    public StockReadService(
        InventoryDbContext db,
        ITenantContext tenantContext,
        ICurrentUserContext currentUser)
    {
        _db = db;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyCollection<StockItemResponse>> GetStockAsync(
        StockListQuery query,
        CancellationToken cancellationToken = default)
    {
        if (_tenantContext.CompanyId is null)
        {
            return [];
        }

        var stockItems = _db.StockItems
            .AsNoTracking()
            .Include(stockItem => stockItem.InventoryOwner)
            .Where(stockItem => stockItem.InventoryOwner.IsActive);

        if (query.ProductId is not null)
        {
            stockItems = stockItems.Where(stockItem =>
                stockItem.ProductId == query.ProductId.Value);
        }

        if (query.InventoryOwnerId is not null)
        {
            stockItems = stockItems.Where(stockItem =>
                stockItem.InventoryOwnerId == query.InventoryOwnerId.Value);
        }

        if (query.LowStockOnly)
        {
            stockItems = stockItems.Where(stockItem => stockItem.ReorderThreshold != null &&
                stockItem.QuantityOnHand - stockItem.QuantityReserved < stockItem.ReorderThreshold);
        }

        stockItems = ApplyRoleScope(stockItems);

        var results = await stockItems
            .OrderBy(stockItem => stockItem.InventoryOwner.OwnerType)
            .ThenBy(stockItem => stockItem.InventoryOwner.DisplayName)
            .ThenBy(stockItem => stockItem.ProductId)
            .ToListAsync(cancellationToken);

        return results
            .Select(stockItem => new StockItemResponse(
                stockItem.StockItemId,
                stockItem.InventoryOwnerId,
                stockItem.InventoryOwner.OwnerType.ToString(),
                stockItem.InventoryOwner.ExternalOwnerId,
                stockItem.InventoryOwner.DisplayName,
                stockItem.ProductId,
                stockItem.BatchId,
                stockItem.QuantityOnHand,
                stockItem.QuantityReserved,
                stockItem.AvailableQuantity,
                stockItem.ReorderThreshold,
                stockItem.UpdatedAt))
            .ToList();
    }

    private IQueryable<Domain.Entities.StockItem> ApplyRoleScope(
        IQueryable<Domain.Entities.StockItem> stockItems)
    {
        if (string.Equals(
            _currentUser.Role,
            "CompanyAdmin",
            StringComparison.Ordinal))
        {
            return stockItems;
        }

        if (string.Equals(
                _currentUser.Role,
                "AgencyOperator",
                StringComparison.Ordinal) &&
            _currentUser.AgencyId is not null)
        {
            var agencyId = _currentUser.AgencyId.Value;

            return stockItems.Where(stockItem =>
                stockItem.InventoryOwner.OwnerType ==
                    InventoryOwnerType.Company ||
                (stockItem.InventoryOwner.OwnerType ==
                    InventoryOwnerType.Agency &&
                 stockItem.InventoryOwner.ExternalOwnerId == agencyId));
        }

        if (string.Equals(
                _currentUser.Role,
                "SalesRep",
                StringComparison.Ordinal) &&
            _currentUser.SalesRepId is not null)
        {
            var salesRepId = _currentUser.SalesRepId.Value;

            return stockItems.Where(stockItem =>
                stockItem.InventoryOwner.OwnerType ==
                    InventoryOwnerType.SalesRep &&
                stockItem.InventoryOwner.ExternalOwnerId == salesRepId);
        }

        // A recognized role without its required scope claim receives no data.
        return stockItems.Where(_ => false);
    }
}
