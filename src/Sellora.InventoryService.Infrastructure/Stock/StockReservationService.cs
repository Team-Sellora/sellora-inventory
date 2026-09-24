using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
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
    private readonly LowStockDetectionService _lowStock;

    public StockReservationService(
        InventoryDbContext db,
        ITenantContext tenantContext,
        IOptions<StockReservationOptions> options,
        LowStockDetectionService lowStock)
    {
        _db = db;
        _tenantContext = tenantContext;

        _reservationTtl = TimeSpan.FromMinutes(
            Math.Max(1, options.Value.TtlMinutes));
        _lowStock = lowStock;
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
                // A null batch means "any batch": aggregate availability
                // across every batch of the product the owner holds. An order
                // is placed by product, not by lot.
                var available = CandidatesFor(stockItems, line)
                    .Sum(item => item.AvailableQuantity);

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
            var candidates = CandidatesFor(stockItems, line);

            var totalAvailable = candidates.Sum(item => item.AvailableQuantity);

            if (totalAvailable < line.Quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                DetachReservationAttempt(reservation.ReservationId);

                return await InsufficientStockAsync(
                    request.InventoryOwnerId,
                    lines,
                    cancellationToken);
            }

            var remaining = line.Quantity;

            foreach (var stockItem in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                // A single line can draw from several batches when the order
                // does not name one, so split the quantity across stock items.
                var take = Math.Min(remaining, stockItem.AvailableQuantity);

                // This conditional UPDATE is the concurrency guarantee:
                // only reserve if sufficient currently available stock remains.
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE stock_item
                    SET quantity_reserved = quantity_reserved + {take},
                        row_version = row_version + 1,
                        updated_at = {now}
                    WHERE stock_item_id = {stockItem.StockItemId}
                      AND quantity_on_hand - quantity_reserved >= {take}
                    """,
                    cancellationToken);

                if (affected != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    DetachReservationAttempt(reservation.ReservationId);

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
                    BatchId = stockItem.BatchId,
                    Quantity = take
                });

                _db.StockMovements.Add(new StockMovement
                {
                    StockMovementId = Guid.NewGuid(),
                    StockItemId = stockItem.StockItemId,
                    ReservationId = reservation.ReservationId,
                    MovementType = StockMovementType.Reserved,
                    OnHandDelta = 0,
                    ReservedDelta = take,
                    ReferenceType = "Order",
                    ReferenceId = reservation.OrderReference,
                    Reason = "Stock reserved for order.",
                    OccurredAt = now
                });

                remaining -= take;
            }
        }

        _db.StockReservations.Add(reservation);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ReserveStockResult.Success(ToResponse(reservation));
        }
        catch (DbUpdateException exception)
     when (IsDuplicateOrderReference(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachReservationAttempt(reservation.ReservationId);

            var persistedReservation = await FindByOrderReferenceAsync(
                request.OrderReference,
                cancellationToken);

            if (persistedReservation is not null)
            {
                return ReserveStockResult.Success(
                    ToResponse(persistedReservation));
            }

            throw;
        }
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

        // Event handlers own the transaction so the event receipt and stock
        // changes commit together. Direct API callers still get a transaction.
        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Serialize confirmation, release and expiry for this reservation.
        // Stock balance checks alone cannot prevent consuming another order's hold.
        var reservation = await _db.StockReservations
            .FromSqlInterpolated($"SELECT * FROM stock_reservation WHERE reservation_id = {reservationId} AND company_id = {_tenantContext.CompanyId.Value} FOR UPDATE")
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

        // A scoped context may already track an older reservation state.
        await _db.Entry(reservation).ReloadAsync(cancellationToken);

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

        foreach (var line in reservation.Lines.OrderBy(line => line.StockItemId))
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
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                // An event caller treats this failure as permanent and rolls
                // back its enclosing transaction, including the receipt.
                DetachReservationAttempt(reservation.ReservationId);

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

            if (movementType == StockMovementType.Sold)
                await _lowStock.EvaluateAfterDecrementAsync(line.StockItemId, cancellationToken);
        }

        reservation.Status = completedStatus;
        reservation.CompletedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ReserveStockResult.Success(ToResponse(reservation));
    }

    private void DetachReservationAttempt(Guid reservationId)
    {
        foreach (var entry in _db.ChangeTracker
            .Entries<StockMovement>()
            .Where(entry =>
                entry.State == EntityState.Added &&
                entry.Entity.ReservationId == reservationId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in _db.ChangeTracker
            .Entries<StockReservationLine>()
            .Where(entry =>
                entry.State == EntityState.Added &&
                entry.Entity.ReservationId == reservationId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in _db.ChangeTracker
            .Entries<StockReservation>()
            .Where(entry =>
                entry.State == EntityState.Added &&
                entry.Entity.ReservationId == reservationId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsDuplicateOrderReference(
        DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgresException.ConstraintName ==
            "uq_stock_reservation_company_order_reference";

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

    private static IReadOnlyCollection<StockItem> CandidatesFor(
        IReadOnlyCollection<StockItem> stockItems,
        ReservationLineRequest line)
    {
        // An order is placed by product, not by lot: when the request does
        // not name a batch, every batch of that product is a candidate and
        // the oldest batch is consumed first (deterministic FIFO).
        var matches = line.BatchId is { } batchId
            ? stockItems.Where(item =>
                item.ProductId == line.ProductId && item.BatchId == batchId)
            : stockItems.Where(item => item.ProductId == line.ProductId);

        return matches
            .OrderBy(item => item.BatchId ?? Guid.Empty)
            .ToList();
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
