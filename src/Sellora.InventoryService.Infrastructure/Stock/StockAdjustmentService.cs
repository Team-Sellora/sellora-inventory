using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Exceptions;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class StockAdjustmentService : IStockAdjustmentService
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserContext _currentUser;

    public StockAdjustmentService(
        InventoryDbContext db,
        ITenantContext tenantContext,
        ICurrentUserContext currentUser)
    {
        _db = db;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    public async Task<AdjustStockResult> AdjustAsync(
        AdjustStockRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_tenantContext.CompanyId is null)
        {
            return AdjustStockResult.TenantNotAvailable();
        }

        if (request.InventoryOwnerId == Guid.Empty)
        {
            return AdjustStockResult.InvalidRequest(
                "Inventory owner is required.");
        }

        if (request.ProductId == Guid.Empty)
        {
            return AdjustStockResult.InvalidRequest(
                "Product is required.");
        }

        if (request.QuantityDelta == 0)
        {
            return AdjustStockResult.InvalidRequest(
                "Adjustment quantity cannot be zero.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return AdjustStockResult.InvalidRequest(
                "A reason is required for every stock adjustment.");
        }

        if (string.IsNullOrWhiteSpace(_currentUser.Subject))
        {
            return AdjustStockResult.CallerNotAuthorized();
        }

        var owner = await _db.InventoryOwners
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.InventoryOwnerId == request.InventoryOwnerId &&
                    candidate.IsActive,
                cancellationToken);

        if (owner is null)
        {
            return AdjustStockResult.InventoryOwnerNotFound(
                request.InventoryOwnerId);
        }

        if (!CanAdjustOwner(owner))
        {
            return AdjustStockResult.CallerNotAuthorized();
        }

        var stockItem = await _db.StockItems
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.InventoryOwnerId == owner.InventoryOwnerId &&
                    candidate.ProductId == request.ProductId &&
                    candidate.BatchId == request.BatchId,
                cancellationToken);

        if (stockItem is null)
        {
            stockItem = new StockItem
            {
                StockItemId = Guid.NewGuid(),
                CompanyId = _tenantContext.CompanyId.Value,
                InventoryOwnerId = owner.InventoryOwnerId,
                ProductId = request.ProductId,
                BatchId = request.BatchId
            };

            _db.StockItems.Add(stockItem);
        }

        var occurredAt = DateTimeOffset.UtcNow;

        var movement = new StockMovement
        {
            StockMovementId = Guid.NewGuid(),
            StockItemId = stockItem.StockItemId,
            MovementType = StockMovementType.Adjustment,
            OnHandDelta = request.QuantityDelta,
            ReservedDelta = 0,
            ActorId = _currentUser.Subject,
            Reason = request.Reason.Trim(),
            OccurredAt = occurredAt
        };

        try
        {
            stockItem.ApplyMovement(movement);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InsufficientStockException)
        {
            return AdjustStockResult.InsufficientStock();
        }
        catch (DbUpdateConcurrencyException)
        {
            return AdjustStockResult.ConcurrencyConflict();
        }
        catch (DbUpdateException exception) when (IsStockItemUniqueConstraintViolation(exception))
        {
            // Two requests can both observe a missing stock item before one
            // creates it. The unique index is the final concurrency guard.
            return AdjustStockResult.ConcurrencyConflict();
        }

        return AdjustStockResult.Success(
            new StockAdjustmentResponse(
                stockItem.StockItemId,
                movement.StockMovementId,
                owner.InventoryOwnerId,
                stockItem.ProductId,
                stockItem.BatchId,
                stockItem.QuantityOnHand,
                stockItem.QuantityReserved,
                stockItem.AvailableQuantity,
                request.QuantityDelta,
                movement.Reason!,
                occurredAt));
    }

    private bool CanAdjustOwner(InventoryOwner owner)
    {
        if (string.Equals(
            _currentUser.Role,
            "CompanyAdmin",
            StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(
                   _currentUser.Role,
                   "AgencyOperator",
                   StringComparison.Ordinal) &&
               _currentUser.AgencyId is not null &&
               owner.OwnerType == InventoryOwnerType.Agency &&
               owner.ExternalOwnerId == _currentUser.AgencyId.Value;
    }

    private static bool IsStockItemUniqueConstraintViolation(
        DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName:
                "uq_stock_item_owner_product_batch" or
                "uq_stock_item_owner_product_without_batch"
        };
}
