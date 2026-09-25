using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Infrastructure.OrderEvents;

public sealed class OrderEventHandler(
    IStockReservationService reservationService,
    ISystemTenantContext systemTenantContext,
    InventoryDbContext db,
    LowStockDetectionService lowStock) : IOrderEventHandler
{
    public Task HandleAsync(OrderConfirmedEvent e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ValidateEnvelope(e.EventId, e.CompanyId, e.EventType, "OrderConfirmed", e.SchemaVersion);
        return ProcessOnceAsync(e.CompanyId, e.EventId, e.EventType, e,
            () => CompleteReservationAsync(e.ReservationId, e.OrderReference, true, cancellationToken),
            cancellationToken);
    }

    public Task HandleAsync(OrderCancelledEvent e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ValidateEnvelope(e.EventId, e.CompanyId, e.EventType, "OrderCancelled", e.SchemaVersion);
        return ProcessOnceAsync(e.CompanyId, e.EventId, e.EventType, e,
            () => CompleteReservationAsync(e.ReservationId, e.OrderReference, false, cancellationToken),
            cancellationToken);
    }

    public Task HandleAsync(ReturnAcceptedEvent e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ValidateEnvelope(e.EventId, e.CompanyId, e.EventType, "ReturnAccepted", e.SchemaVersion);
        if (e.InventoryOwnerId == Guid.Empty || string.IsNullOrWhiteSpace(e.ReturnReference) ||
            e.ReturnReference.Length > 100 || e.Lines is null || e.Lines.Count == 0 ||
            e.Lines.Any(line => line is null || line.ProductId == Guid.Empty ||
                line.BatchId == Guid.Empty || line.Quantity <= 0))
            throw new InvalidInventoryEventException("ReturnAccepted contains invalid owner, reference or lines.");

        return ProcessOnceAsync(e.CompanyId, e.EventId, e.EventType, e,
            () => RestoreAsync(e, cancellationToken), cancellationToken);
    }

    private async Task ProcessOnceAsync<T>(Guid companyId, Guid eventId, string eventType,
        T payload, Func<Task> apply, CancellationToken cancellationToken)
    {
        using var tenantScope = systemTenantContext.BeginSystemTenantScope(companyId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));

        // Concurrent deliveries wait for this unique insert. Receipt and stock
        // commit together; a rollback permits the next delivery to retry.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO processed_inventory_event
                (company_id, event_id, event_type, payload_hash, processed_at)
            VALUES ({companyId}, {eventId}, {eventType}, {hash}, {DateTimeOffset.UtcNow})
            ON CONFLICT (company_id, event_id) DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
        {
            var receipt = await db.ProcessedInventoryEvents.AsNoTracking()
                .SingleAsync(e => e.EventId == eventId, cancellationToken);
            if (receipt.EventType != eventType || receipt.PayloadHash != hash)
                throw new InvalidInventoryEventException("EventId was reused with a different payload.");
        }
        else
        {
            await apply();
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteReservationAsync(Guid reservationId, string orderReference,
        bool confirm, CancellationToken cancellationToken)
    {
        if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(orderReference))
            throw new InvalidInventoryEventException("A reservation and order reference are required.");

        var reservation = await db.StockReservations.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ReservationId == reservationId, cancellationToken);
        if (reservation is null || reservation.OrderReference != orderReference.Trim())
            throw new InvalidInventoryEventException("Unknown reservation or mismatched order reference.");

        if (!await db.InventoryOwners.AnyAsync(o =>
            o.InventoryOwnerId == reservation.InventoryOwnerId, cancellationToken))
            throw new InvalidInventoryEventException("The resolved inventory owner is unknown.");

        // US-E4-5: a cancelled order may already have its stock sold (a
        // scheduled delivery commits at placement), so cancellation returns
        // it rather than failing on a confirmed reservation.
        var result = confirm
            ? await reservationService.ConfirmAsync(reservationId, cancellationToken)
            : await reservationService.CancelForOrderAsync(reservationId, cancellationToken);

        var alreadyApplied = confirm
            ? result.Outcome == ReservationOutcome.ReservationAlreadyConfirmed
            : result.Outcome == ReservationOutcome.ReservationAlreadyReleased;

        if (result.Outcome != ReservationOutcome.Success && !alreadyApplied)
            throw new InvalidInventoryEventException($"Reservation transition rejected: {result.Outcome}.");
    }

    private async Task RestoreAsync(ReturnAcceptedEvent e, CancellationToken cancellationToken)
    {
        // Serialize returns per owner, including creation of missing stock rows.
        var owner = await db.InventoryOwners
            .FromSqlInterpolated($"SELECT * FROM inventory_owner WHERE inventory_owner_id = {e.InventoryOwnerId} AND company_id = {e.CompanyId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
            throw new InvalidInventoryEventException("The return inventory owner is unknown.");

        var lines = e.Lines.GroupBy(line => new { line.ProductId, line.BatchId })
            .Select(group => new { group.Key.ProductId, group.Key.BatchId,
                Quantity = group.Sum(line => (long)line.Quantity) })
            .OrderBy(line => line.ProductId).ThenBy(line => line.BatchId).ToArray();

        foreach (var line in lines)
        {
            var stock = await db.StockItems.SingleOrDefaultAsync(item =>
                item.InventoryOwnerId == e.InventoryOwnerId &&
                item.ProductId == line.ProductId && item.BatchId == line.BatchId,
                cancellationToken);

            if (line.Quantity > int.MaxValue ||
                line.Quantity + (stock?.QuantityOnHand ?? 0) > int.MaxValue)
                throw new InvalidInventoryEventException("Return quantity exceeds the supported stock balance.");

            if (stock is null)
            {
                stock = new StockItem
                {
                    StockItemId = Guid.NewGuid(), CompanyId = e.CompanyId,
                    InventoryOwnerId = e.InventoryOwnerId,
                    ProductId = line.ProductId, BatchId = line.BatchId
                };
                db.StockItems.Add(stock);
            }

            stock.ApplyMovement(new StockMovement
            {
                StockMovementId = Guid.NewGuid(), StockItemId = stock.StockItemId,
                MovementType = StockMovementType.Returned,
                OnHandDelta = (int)line.Quantity, ReservedDelta = 0,
                ReferenceType = "Return", ReferenceId = e.ReturnReference.Trim(),
                Reason = "Stock restored for accepted return.", OccurredAt = DateTimeOffset.UtcNow
            });
            lowStock.RearmIfRestocked(stock);
        }
    }

    private static void ValidateEnvelope(Guid eventId, Guid companyId, string eventType,
        string expectedType, string schemaVersion)
    {
        if (eventId == Guid.Empty || companyId == Guid.Empty || eventType != expectedType ||
            schemaVersion != "1.0")
            throw new InvalidInventoryEventException("Invalid event envelope or unsupported schemaVersion (expected 1.0).");
    }
}
