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
    InventoryDbContext db) : IOrderEventHandler
{
    public Task HandleAsync(OrderConfirmedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ValidateEnvelope(@event.EventId, @event.CompanyId, @event.EventType, "OrderConfirmed", @event.SchemaVersion);
        return ProcessOnceAsync(@event.CompanyId, @event.EventId, @event.EventType, @event,
            () => CompleteReservationAsync(@event.ReservationId, @event.OrderReference, true, cancellationToken),
            cancellationToken);
    }

    public Task HandleAsync(OrderCancelledEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ValidateEnvelope(@event.EventId, @event.CompanyId, @event.EventType, "OrderCancelled", @event.SchemaVersion);
        return ProcessOnceAsync(@event.CompanyId, @event.EventId, @event.EventType, @event,
            () => CompleteReservationAsync(@event.ReservationId, @event.OrderReference, false, cancellationToken),
            cancellationToken);
    }

    public Task HandleAsync(ReturnAcceptedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ValidateEnvelope(@event.EventId, @event.CompanyId, @event.EventType, "ReturnAccepted", @event.SchemaVersion);
        if (@event.InventoryOwnerId == Guid.Empty || string.IsNullOrWhiteSpace(@event.ReturnReference) ||
            @event.ReturnReference.Length > 100 || @event.Lines is null || @event.Lines.Count == 0 ||
            @event.Lines.Any(line => line is null || line.ProductId == Guid.Empty ||
                line.BatchId == Guid.Empty || line.Quantity <= 0))
            throw new InvalidInventoryEventException("ReturnAccepted contains invalid owner, reference or lines.");

        return ProcessOnceAsync(@event.CompanyId, @event.EventId, @event.EventType, @event,
            () => RestoreAsync(@event, cancellationToken), cancellationToken);
    }

    public Task HandleAsync(VanStockReturnedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ValidateEnvelope(@event.EventId, @event.CompanyId, @event.EventType, "VanStockReturned", @event.SchemaVersion);
        if (@event.SalesRepId == Guid.Empty || @event.AgencyId == Guid.Empty || @event.VanInventoryOwnerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(@event.ReturnReference) || @event.ReturnReference.Length > 100 ||
            @event.Lines is null || @event.Lines.Count == 0 ||
            @event.Lines.Any(line => line is null || line.ProductId == Guid.Empty || line.AcceptedQuantity < 0))
            throw new InvalidInventoryEventException("VanStockReturned contains an invalid rep, agency, owner, reference or lines.");

        return ProcessOnceAsync(@event.CompanyId, @event.EventId, @event.EventType, @event,
            () => TransferVanStockAsync(@event, cancellationToken), cancellationToken);
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
            LowStockDetectionService.RearmIfRestocked(stock);
        }
    }

    /// <summary>
    /// US-E4-6: moves each accepted quantity from the rep's van to the
    /// agency, batch by batch (oldest first, the same order reservations
    /// use), in the handler's transaction. The van side is a conditional
    /// update that never goes below held stock, so a van return can never
    /// create stock out of nothing or take stock promised to a cash sale.
    /// </summary>
    private async Task TransferVanStockAsync(VanStockReturnedEvent e, CancellationToken cancellationToken)
    {
        var van = await LockVanOwnerAsync(e, cancellationToken);
        var agencyOwnerId = await LockAgencyOwnerAsync(e, cancellationToken);

        var transfer = new VanTransfer(e, van.InventoryOwnerId, agencyOwnerId,
            e.ReturnReference.Trim(), DateTimeOffset.UtcNow,
            new Dictionary<(Guid ProductId, Guid? BatchId), StockItem>());

        var lines = e.Lines
            .GroupBy(line => line.ProductId)
            .Select(group => (ProductId: group.Key, Quantity: group.Sum(line => (long)line.AcceptedQuantity)))
            .Where(line => line.Quantity > 0)
            .OrderBy(line => line.ProductId)
            .ToArray();

        foreach (var (productId, quantity) in lines)
            await TransferProductAsync(transfer, productId, quantity, cancellationToken);

        foreach (var agencyItem in transfer.Credited.Values)
            LowStockDetectionService.RearmIfRestocked(agencyItem);
    }

    private async Task<InventoryOwner> LockVanOwnerAsync(VanStockReturnedEvent e, CancellationToken cancellationToken)
    {
        var van = await db.InventoryOwners
            .FromSqlInterpolated($"SELECT * FROM inventory_owner WHERE inventory_owner_id = {e.VanInventoryOwnerId} AND company_id = {e.CompanyId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (van is null || van.OwnerType != InventoryOwnerType.SalesRep || van.ExternalOwnerId != e.SalesRepId)
            throw new InvalidInventoryEventException("The van owner is unknown or does not belong to the returning rep.");

        return van;
    }

    private async Task<Guid> LockAgencyOwnerAsync(VanStockReturnedEvent e, CancellationToken cancellationToken)
    {
        var agencyOwnerId = await db.InventoryOwners
            .Where(owner => owner.OwnerType == InventoryOwnerType.Agency && owner.ExternalOwnerId == e.AgencyId)
            .Select(owner => (Guid?)owner.InventoryOwnerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (agencyOwnerId is null)
            throw new InvalidInventoryEventException("The rep's agency has no inventory owner to receive the stock.");

        // Lock the agency owner too, so missing stock rows are created once.
        await db.InventoryOwners
            .FromSqlInterpolated($"SELECT * FROM inventory_owner WHERE inventory_owner_id = {agencyOwnerId.Value} AND company_id = {e.CompanyId} FOR UPDATE")
            .SingleAsync(cancellationToken);

        return agencyOwnerId.Value;
    }

    private async Task TransferProductAsync(VanTransfer transfer, Guid productId, long quantity,
        CancellationToken cancellationToken)
    {
        var vanItems = await db.StockItems.AsNoTracking()
            .Where(item => item.InventoryOwnerId == transfer.VanOwnerId && item.ProductId == productId)
            .ToListAsync(cancellationToken);
        vanItems = vanItems.OrderBy(item => item.BatchId ?? Guid.Empty).ToList();

        var held = vanItems.Sum(item => (long)item.AvailableQuantity);
        if (held < quantity)
            throw new InvalidInventoryEventException(
                $"Van return {transfer.Reference}: the van holds only {held} of product {productId}, {quantity} were accepted.");

        var remaining = (int)quantity;

        foreach (var vanItem in vanItems)
        {
            var take = Math.Min(remaining, vanItem.AvailableQuantity);
            if (take <= 0)
                continue;

            await DebitVanItemAsync(transfer, vanItem, take, cancellationToken);
            await CreditAgencyAsync(transfer, productId, vanItem.BatchId, take, cancellationToken);

            remaining -= take;
            if (remaining == 0)
                break;
        }

        if (remaining > 0)
            throw new InvalidOperationException(
                $"Van return {transfer.Reference}: van stock changed while transferring; retrying.");
    }

    private async Task DebitVanItemAsync(VanTransfer transfer, StockItem vanItem, int take,
        CancellationToken cancellationToken)
    {
        var debited = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE stock_item
            SET quantity_on_hand = quantity_on_hand - {take},
                row_version = row_version + 1,
                updated_at = {transfer.Now}
            WHERE stock_item_id = {vanItem.StockItemId}
              AND quantity_on_hand - quantity_reserved >= {take}
            """, cancellationToken);

        // Someone reserved from the van since we read it: roll back and
        // let the consumer retry from the committed offset.
        if (debited != 1)
            throw new InvalidOperationException(
                $"Van stock {vanItem.StockItemId} changed during van return {transfer.Reference}; retrying.");

        db.StockMovements.Add(new StockMovement
        {
            StockMovementId = Guid.NewGuid(), StockItemId = vanItem.StockItemId,
            MovementType = StockMovementType.Transferred,
            OnHandDelta = -take, ReservedDelta = 0,
            ReferenceType = "VanReturn", ReferenceId = transfer.Reference,
            ActorId = $"sales-rep:{transfer.Event.SalesRepId}",
            Reason = "End-of-route van return to agency.", OccurredAt = transfer.Now
        });
    }

    private async Task CreditAgencyAsync(VanTransfer transfer, Guid productId, Guid? batchId, int take,
        CancellationToken cancellationToken)
    {
        var agencyItem = await GetOrAddAgencyItemAsync(transfer, productId, batchId, cancellationToken);

        if ((long)agencyItem.QuantityOnHand + take > int.MaxValue)
            throw new InvalidInventoryEventException("Van return quantity exceeds the supported stock balance.");

        // Same batch on the agency side, so expiry tracking follows the goods.
        agencyItem.ApplyMovement(new StockMovement
        {
            StockMovementId = Guid.NewGuid(), StockItemId = agencyItem.StockItemId,
            MovementType = StockMovementType.Transferred,
            OnHandDelta = take, ReservedDelta = 0,
            ReferenceType = "VanReturn", ReferenceId = transfer.Reference,
            ActorId = $"sales-rep:{transfer.Event.SalesRepId}",
            Reason = "End-of-route van return from sales rep.", OccurredAt = transfer.Now
        });
    }

    private async Task<StockItem> GetOrAddAgencyItemAsync(VanTransfer transfer, Guid productId, Guid? batchId,
        CancellationToken cancellationToken)
    {
        var key = (productId, batchId);
        if (transfer.Credited.TryGetValue(key, out var agencyItem))
            return agencyItem;

        agencyItem = await db.StockItems.SingleOrDefaultAsync(item =>
            item.InventoryOwnerId == transfer.AgencyOwnerId &&
            item.ProductId == productId && item.BatchId == batchId,
            cancellationToken);

        if (agencyItem is null)
        {
            agencyItem = new StockItem
            {
                StockItemId = Guid.NewGuid(), CompanyId = transfer.Event.CompanyId,
                InventoryOwnerId = transfer.AgencyOwnerId,
                ProductId = productId, BatchId = batchId
            };
            db.StockItems.Add(agencyItem);
        }

        transfer.Credited[key] = agencyItem;
        return agencyItem;
    }

    private sealed record VanTransfer(
        VanStockReturnedEvent Event,
        Guid VanOwnerId,
        Guid AgencyOwnerId,
        string Reference,
        DateTimeOffset Now,
        Dictionary<(Guid ProductId, Guid? BatchId), StockItem> Credited);

    private static void ValidateEnvelope(Guid eventId, Guid companyId, string eventType,
        string expectedType, string schemaVersion)
    {
        if (eventId == Guid.Empty || companyId == Guid.Empty || eventType != expectedType ||
            schemaVersion != "1.0")
            throw new InvalidInventoryEventException("Invalid event envelope or unsupported schemaVersion (expected 1.0).");
    }
}
