using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class StockReservationService : IStockReservationService
{
    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly TimeSpan _reservationTtl;

    public StockReservationService(
        InventoryDbContext db,
        ITenantContext tenantContext,
        IOptions<StockReservationOptions> options)
    {
        _db = db;
        _tenantContext = tenantContext;

        _reservationTtl = TimeSpan.FromMinutes(
            Math.Max(1, options.Value.TtlMinutes));
    }

    public async Task<IReadOnlyCollection<StockAvailability>>
        CheckAvailabilityAsync(
            CheckAvailabilityRequest request,
            CancellationToken cancellationToken = default)
    {
        var lines = NormalizeLines(request.Lines);

        if (_tenantContext.CompanyId is null ||
            request.InventoryOwnerId == Guid.Empty ||
            lines.Count == 0)
        {
            return [];
        }

        var productIds = lines
            .Select(line => line.ProductId)
            .Distinct()
            .ToArray();

        var stockItems = await _db.StockItems
            .AsNoTracking()
            .Where(item =>
                item.InventoryOwnerId == request.InventoryOwnerId &&
                productIds.Contains(item.ProductId))
            .ToListAsync(cancellationToken);

        return lines
            .Select(line =>
            {
                var stockItem = stockItems.SingleOrDefault(item =>
                    item.ProductId == line.ProductId &&
                    item.BatchId == line.BatchId);

                var available = stockItem?.AvailableQuantity ?? 0;

                return new StockAvailability(
                    line.ProductId,
                    line.BatchId,
                    line.Quantity,
                    available,
                    available >= line.Quantity);
            })
            .ToList();
    }

    public async Task<ReserveStockResult> ReserveAsync(
        ReserveStockRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_tenantContext.CompanyId is null)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.TenantNotAvailable,
                "A company identifier is required.");
        }

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InvalidRequest,
                validationError);
        }

        var lines = NormalizeLines(request.Lines);

        var ownerExists = await _db.InventoryOwners
            .AsNoTracking()
            .AnyAsync(owner =>
                owner.InventoryOwnerId == request.InventoryOwnerId &&
                owner.IsActive,
                cancellationToken);

        if (!ownerExists)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InventoryOwnerNotFound,
                "The requested inventory owner was not found.");
        }

        var existing = await FindByOrderReferenceAsync(
            request.OrderReference,
            cancellationToken);

        if (existing is not null)
        {
            return ReserveStockResult.Success(ToResponse(existing));
        }

        var availability = await CheckAvailabilityAsync(
            new CheckAvailabilityRequest(
                request.InventoryOwnerId,
                lines),
            cancellationToken);

        var initialShortages = availability
            .Where(item => !item.IsAvailable)
            .Select(item => new ReservationShortage(
                item.ProductId,
                item.BatchId,
                item.RequestedQuantity,
                item.AvailableQuantity))
            .ToList();

        if (initialShortages.Count > 0)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InsufficientStock,
                "One or more requested products are unavailable.",
                initialShortages);
        }

        var now = DateTimeOffset.UtcNow;
        var reservation = new StockReservation
        {
            ReservationId = Guid.NewGuid(),
            CompanyId = _tenantContext.CompanyId.Value,
            OrderReference = request.OrderReference.Trim(),
            InventoryOwnerId = request.InventoryOwnerId,
            Status = ReservationStatus.Active,
            CreatedAt = now,
            ExpiresAt = now.Add(_reservationTtl)
        };

        await using var transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken);

        // Recheck after opening the transaction to preserve idempotency when
        // retries race with a successful first request.
        existing = await FindByOrderReferenceAsync(
            request.OrderReference,
            cancellationToken);

        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReserveStockResult.Success(ToResponse(existing));
        }

        var stockItems = await GetStockItemsAsync(
            request.InventoryOwnerId,
            lines,
            cancellationToken);

        foreach (var line in lines
            .OrderBy(line => line.ProductId)
            .ThenBy(line => line.BatchId))
        {
            var stockItem = stockItems.SingleOrDefault(item =>
                item.ProductId == line.ProductId &&
                item.BatchId == line.BatchId);

            if (stockItem is null)
            {
                await transaction.RollbackAsync(cancellationToken);

                return await InsufficientStockAsync(
                    request.InventoryOwnerId,
                    lines,
                    cancellationToken);
            }

            // This conditional UPDATE is the concurrency guarantee:
            // only reserve if sufficient currently available stock remains.
            var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE stock_item
                SET quantity_reserved = quantity_reserved + {line.Quantity},
                    row_version = row_version + 1,
                    updated_at = {now}
                WHERE stock_item_id = {stockItem.StockItemId}
                  AND quantity_on_hand - quantity_reserved >= {line.Quantity}
                """,
                cancellationToken);

            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);

                return await InsufficientStockAsync(
                    request.InventoryOwnerId,
                    lines,
                    cancellationToken);
            }

            reservation.Lines.Add(new StockReservationLine
            {
                StockReservationLineId = Guid.NewGuid(),
                ReservationId = reservation.ReservationId,
                StockItemId = stockItem.StockItemId,
                ProductId = line.ProductId,
                BatchId = line.BatchId,
                Quantity = line.Quantity
            });

            _db.StockMovements.Add(new StockMovement
            {
                StockMovementId = Guid.NewGuid(),
                StockItemId = stockItem.StockItemId,
                ReservationId = reservation.ReservationId,
                MovementType = StockMovementType.Reserved,
                OnHandDelta = 0,
                ReservedDelta = line.Quantity,
                ReferenceType = "Order",
                ReferenceId = reservation.OrderReference,
                Reason = "Stock reserved for order.",
                OccurredAt = now
            });
        }

        _db.StockReservations.Add(reservation);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ReserveStockResult.Success(ToResponse(reservation));
    }

    public async Task<ReserveStockResult> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        return await CompleteAsync(
            reservationId,
            ReservationStatus.Released,
            StockMovementType.Released,
            cancellationToken);
    }

    public async Task<ReserveStockResult> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        return await CompleteAsync(
            reservationId,
            ReservationStatus.Confirmed,
            StockMovementType.Sold,
            cancellationToken);
    }

    private async Task<ReserveStockResult> CompleteAsync(
        Guid reservationId,
        ReservationStatus completedStatus,
        StockMovementType movementType,
        CancellationToken cancellationToken)
    {
        if (_tenantContext.CompanyId is null)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.TenantNotAvailable,
                "A company identifier is required.");
        }

        var reservation = await _db.StockReservations
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(
                candidate => candidate.ReservationId == reservationId,
                cancellationToken);

        if (reservation is null)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.ReservationNotFound,
                "The stock reservation was not found.");
        }

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.ReservationAlreadyConfirmed,
                "The stock reservation has already been confirmed.");
        }

        if (reservation.Status != ReservationStatus.Active)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.ReservationAlreadyReleased,
                "The stock reservation is no longer active.");
        }

        var now = DateTimeOffset.UtcNow;

        await using var transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var line in reservation.Lines)
        {
            var affected = movementType == StockMovementType.Sold
                ? await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE stock_item
                    SET quantity_on_hand = quantity_on_hand - {line.Quantity},
                        quantity_reserved = quantity_reserved - {line.Quantity},
                        row_version = row_version + 1,
                        updated_at = {now}
                    WHERE stock_item_id = {line.StockItemId}
                      AND quantity_on_hand >= {line.Quantity}
                      AND quantity_reserved >= {line.Quantity}
                    """,
                    cancellationToken)
                : await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE stock_item
                    SET quantity_reserved = quantity_reserved - {line.Quantity},
                        row_version = row_version + 1,
                        updated_at = {now}
                    WHERE stock_item_id = {line.StockItemId}
                      AND quantity_reserved >= {line.Quantity}
                    """,
                    cancellationToken);

            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);

                return ReserveStockResult.Failure(
                    ReservationOutcome.InsufficientStock,
                    "The reservation could not be completed safely.");
            }

            _db.StockMovements.Add(new StockMovement
            {
                StockMovementId = Guid.NewGuid(),
                StockItemId = line.StockItemId,
                ReservationId = reservation.ReservationId,
                MovementType = movementType,
                OnHandDelta = movementType == StockMovementType.Sold
                    ? -line.Quantity
                    : 0,
                ReservedDelta = -line.Quantity,
                ReferenceType = "Order",
                ReferenceId = reservation.OrderReference,
                Reason = movementType == StockMovementType.Sold
                    ? "Reserved stock sold for confirmed order."
                    : "Stock reservation released.",
                OccurredAt = now
            });
        }

        reservation.Status = completedStatus;
        reservation.CompletedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ReserveStockResult.Success(ToResponse(reservation));
    }

    private async Task<ReserveStockResult> InsufficientStockAsync(
        Guid inventoryOwnerId,
        IReadOnlyCollection<ReservationLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var availability = await CheckAvailabilityAsync(
            new CheckAvailabilityRequest(inventoryOwnerId, lines),
            cancellationToken);

        var shortages = availability
            .Where(item => !item.IsAvailable)
            .Select(item => new ReservationShortage(
                item.ProductId,
                item.BatchId,
                item.RequestedQuantity,
                item.AvailableQuantity))
            .ToList();

        return ReserveStockResult.Failure(
            ReservationOutcome.InsufficientStock,
            "One or more requested products are unavailable.",
            shortages);
    }

    private async Task<StockReservation?> FindByOrderReferenceAsync(
        string orderReference,
        CancellationToken cancellationToken)
    {
        return await _db.StockReservations
            .AsNoTracking()
            .Include(reservation => reservation.Lines)
            .SingleOrDefaultAsync(
                reservation =>
                    reservation.OrderReference == orderReference.Trim(),
                cancellationToken);
    }

    private async Task<IReadOnlyCollection<StockItem>> GetStockItemsAsync(
        Guid inventoryOwnerId,
        IReadOnlyCollection<ReservationLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var productIds = lines
            .Select(line => line.ProductId)
            .Distinct()
            .ToArray();

        return await _db.StockItems
            .AsNoTracking()
            .Where(item =>
                item.InventoryOwnerId == inventoryOwnerId &&
                productIds.Contains(item.ProductId))
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyCollection<ReservationLineRequest> NormalizeLines(
        IReadOnlyCollection<ReservationLineRequest> lines) =>
        lines
            .GroupBy(line => new { line.ProductId, line.BatchId })
            .Select(group => new ReservationLineRequest(
                group.Key.ProductId,
                group.Key.BatchId,
                group.Sum(line => line.Quantity)))
            .ToList();

    private static string? ValidateRequest(ReserveStockRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OrderReference))
        {
            return "Order reference is required.";
        }

        if (request.InventoryOwnerId == Guid.Empty)
        {
            return "Inventory owner is required.";
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return "At least one stock line is required.";
        }

        if (request.Lines.Any(line =>
            line.ProductId == Guid.Empty || line.Quantity <= 0))
        {
            return "Every stock line requires a product and positive quantity.";
        }

        return null;
    }

    private static StockReservationResponse ToResponse(
        StockReservation reservation) =>
        new(
            reservation.ReservationId,
            reservation.OrderReference,
            reservation.InventoryOwnerId,
            reservation.Status.ToString(),
            reservation.ExpiresAt,
            reservation.Lines
                .Select(line => new ReservationLineResponse(
                    line.StockItemId,
                    line.ProductId,
                    line.BatchId,
                    line.Quantity))
                .ToList());
}