using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class StockThresholdService(InventoryDbContext db, ICurrentUserContext user)
{
    public async Task<StockItemResponse?> UpdateAsync(UpdateReorderThresholdRequest request, CancellationToken cancellationToken)
    {
        if (request.ReorderThreshold is < 0) throw new ArgumentOutOfRangeException(nameof(request.ReorderThreshold));
        var item = await db.StockItems.Include(x => x.InventoryOwner)
            .SingleOrDefaultAsync(x => x.StockItemId == request.StockItemId, cancellationToken);
        if (item is null) return null;
        var allowed = user.Role == "CompanyAdmin" || (user.Role == "AgencyOperator" && user.AgencyId != null &&
            item.InventoryOwner.OwnerType == InventoryOwnerType.Agency && item.InventoryOwner.ExternalOwnerId == user.AgencyId);
        if (!allowed) throw new UnauthorizedAccessException();
        item.ReorderThreshold = request.ReorderThreshold;
        if (request.ReorderThreshold is int threshold && item.AvailableQuantity > threshold) item.LowStockNotified = false;
        await db.SaveChangesAsync(cancellationToken);
        return new StockItemResponse(item.StockItemId, item.InventoryOwnerId, item.InventoryOwner.OwnerType.ToString(),
            item.InventoryOwner.ExternalOwnerId, item.InventoryOwner.DisplayName, item.ProductId, item.BatchId,
            item.QuantityOnHand, item.QuantityReserved, item.AvailableQuantity, item.ReorderThreshold, item.UpdatedAt);
    }
}
