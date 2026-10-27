using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.OrderEvents;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

/// <summary>OrderConfirmed, OrderCancelled and ReturnAccepted against PostgreSQL.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class OrderEventHandlerTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly InventoryOwner _owner;
    private readonly StockItem _stock;

    public OrderEventHandlerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _owner = TestData.Owner(_companyId, InventoryOwnerType.Agency);
        _stock = TestData.Stock(_owner, 10, batchId: Guid.NewGuid(), reorderThreshold: 8);
    }

    private async Task SeedAsync()
    {
        await using var db = _fixture.CreateContext(_companyId);
        db.InventoryOwners.Add(_owner);
        db.StockItems.Add(_stock);
        await db.SaveChangesAsync();
    }

    private StockReservationService Reservations(InventoryDbContext db) => new(
        db, new FixedTenantContext(_companyId),
        Options.Create(new StockReservationOptions()), new LowStockDetectionService(db));

    private async Task<StockReservationResponse> ReserveAsync(string reference, int quantity)
    {
        await using var db = _fixture.CreateContext(_companyId);
        var result = await Reservations(db).ReserveAsync(new ReserveStockRequest(
            reference, _owner.InventoryOwnerId,
            new[] { new ReservationLineRequest(_stock.ProductId, null, quantity) }));
        Assert.Equal(ReservationOutcome.Success, result.Outcome);
        return result.Reservation!;
    }

    private async Task HandleAsync(Func<OrderEventHandler, Task> handle)
    {
        await using var db = _fixture.CreateContext(_companyId);
        await handle(new OrderEventHandler(Reservations(db), new NoOpSystemTenantContext(), db));
    }

    private async Task<StockItem> StockAsync()
    {
        await using var db = _fixture.CreateContext(_companyId);
        return await db.StockItems.AsNoTracking().SingleAsync(item => item.StockItemId == _stock.StockItemId);
    }

    private OrderConfirmedEvent Confirmed(Guid reservationId, string reference, Guid? eventId = null) => new(
        eventId ?? Guid.NewGuid(), "OrderConfirmed", "1.0", _companyId, Guid.NewGuid(),
        reservationId, reference, DateTimeOffset.UtcNow, "order-event-test");

    private OrderCancelledEvent Cancelled(Guid reservationId, string reference) => new(
        Guid.NewGuid(), "OrderCancelled", "1.0", _companyId, Guid.NewGuid(),
        reservationId, reference, DateTimeOffset.UtcNow, "order-event-test");

    private ReturnAcceptedEvent Returned(params ReturnAcceptedLine[] lines) => new(
        Guid.NewGuid(), "ReturnAccepted", "1.0", _companyId, Guid.NewGuid(),
        _owner.InventoryOwnerId, " RET-0001 ", lines, DateTimeOffset.UtcNow, "order-event-test");

    [Fact]
    public async Task Order_confirmed_sells_the_reservation_once_and_raises_low_stock()
    {
        await SeedAsync();
        var reservation = await ReserveAsync("ORD-CONFIRM-1", 4);
        var confirmed = Confirmed(reservation.ReservationId, " ORD-CONFIRM-1 ");

        await HandleAsync(handler => handler.HandleAsync(confirmed));
        await HandleAsync(handler => handler.HandleAsync(confirmed));

        var stock = await StockAsync();
        Assert.Equal(6, stock.QuantityOnHand);
        Assert.Equal(0, stock.QuantityReserved);
        Assert.True(stock.LowStockNotified);

        await using var db = _fixture.CreateContext(_companyId);
        Assert.Equal(1, await db.ProcessedInventoryEvents.CountAsync(e => e.EventId == confirmed.EventId));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.AggregateId == _stock.StockItemId));
    }

    [Fact]
    public async Task A_reused_event_id_with_a_different_payload_is_rejected()
    {
        await SeedAsync();
        var reservation = await ReserveAsync("ORD-REUSED-1", 1);
        var eventId = Guid.NewGuid();
        await HandleAsync(handler => handler.HandleAsync(Confirmed(reservation.ReservationId, "ORD-REUSED-1", eventId)));

        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(handler =>
            handler.HandleAsync(Confirmed(reservation.ReservationId, "ORD-REUSED-1", eventId) with { CorrelationId = "other" })));
    }

    [Fact]
    public async Task Order_cancelled_releases_a_held_reservation_and_returns_sold_stock()
    {
        await SeedAsync();
        var held = await ReserveAsync("ORD-CANCEL-HELD", 2);
        var sold = await ReserveAsync("ORD-CANCEL-SOLD", 3);
        await HandleAsync(handler => handler.HandleAsync(Confirmed(sold.ReservationId, "ORD-CANCEL-SOLD")));

        await HandleAsync(handler => handler.HandleAsync(Cancelled(held.ReservationId, "ORD-CANCEL-HELD")));
        await HandleAsync(handler => handler.HandleAsync(Cancelled(sold.ReservationId, "ORD-CANCEL-SOLD")));
        // A second cancellation of the same order is a harmless replay.
        await HandleAsync(handler => handler.HandleAsync(Cancelled(sold.ReservationId, "ORD-CANCEL-SOLD")));

        var stock = await StockAsync();
        Assert.Equal(10, stock.QuantityOnHand);
        Assert.Equal(0, stock.QuantityReserved);
    }

    [Fact]
    public async Task Order_events_for_unknown_or_mismatched_reservations_are_rejected()
    {
        await SeedAsync();
        var reservation = await ReserveAsync("ORD-MISMATCH-1", 1);

        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(handler =>
            handler.HandleAsync(Confirmed(Guid.NewGuid(), "ORD-MISMATCH-1"))));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(handler =>
            handler.HandleAsync(Confirmed(reservation.ReservationId, "ORD-SOMETHING-ELSE"))));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(handler =>
            handler.HandleAsync(Cancelled(Guid.Empty, "ORD-MISMATCH-1"))));
    }

    [Fact]
    public async Task Returns_restore_existing_stock_and_create_missing_rows()
    {
        await SeedAsync();
        var newProduct = Guid.NewGuid();
        var newBatch = Guid.NewGuid();

        await HandleAsync(handler => handler.HandleAsync(Returned(
            new ReturnAcceptedLine(_stock.ProductId, _stock.BatchId, 2),
            new ReturnAcceptedLine(_stock.ProductId, _stock.BatchId, 1),
            new ReturnAcceptedLine(newProduct, newBatch, 5))));

        Assert.Equal(13, (await StockAsync()).QuantityOnHand);

        await using var db = _fixture.CreateContext(_companyId);
        var created = await db.StockItems.SingleAsync(item => item.ProductId == newProduct);
        Assert.Equal(5, created.QuantityOnHand);
        Assert.Equal(newBatch, created.BatchId);
        Assert.True(await db.StockMovements.AnyAsync(m => m.ReferenceId == "RET-0001" && m.MovementType == StockMovementType.Returned));
    }

    [Fact]
    public async Task Returns_to_an_unknown_owner_are_rejected()
    {
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(handler =>
            handler.HandleAsync(Returned(new ReturnAcceptedLine(Guid.NewGuid(), null, 1)))));
    }

    [Fact]
    public async Task Invalid_envelopes_and_payloads_are_rejected_before_any_work()
    {
        await using var db = _fixture.CreateContext(_companyId);
        var handler = new OrderEventHandler(Reservations(db), new NoOpSystemTenantContext(), db);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync((OrderConfirmedEvent)null!));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            handler.HandleAsync(Confirmed(Guid.NewGuid(), "ORD-1") with { SchemaVersion = "2.0" }));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            handler.HandleAsync(Cancelled(Guid.NewGuid(), "ORD-1") with { EventType = "OrderConfirmed" }));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            handler.HandleAsync(Returned()));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            handler.HandleAsync(Returned(new ReturnAcceptedLine(Guid.NewGuid(), null, 0))));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            handler.HandleAsync(new VanStockReturnedEvent(Guid.NewGuid(), "VanStockReturned", "1.0", _companyId,
                Guid.NewGuid(), "VR-1", Guid.Empty, Guid.NewGuid(), Guid.NewGuid(),
                new[] { new VanStockReturnedLine(Guid.NewGuid(), 1) }, DateTimeOffset.UtcNow, "c")));
    }
}
