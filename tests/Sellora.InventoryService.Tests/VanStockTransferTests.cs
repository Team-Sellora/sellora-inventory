using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.OrderEvents;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

/// <summary>
/// US-E4-6 / DoD 4: VanStockReturned moves the accepted quantity from the
/// rep's van to the agency, batch by batch, exactly once.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class VanStockTransferTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _repId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _vanOwnerId = Guid.NewGuid();
    private readonly Guid _agencyOwnerId = Guid.NewGuid();
    private readonly Guid _productId = Guid.NewGuid();
    private readonly Guid _olderBatch = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly Guid _newerBatch = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public VanStockTransferTests(PostgreSqlFixture fixture) => _fixture = fixture;

    /// <summary>Van holds 30 of one product: 20 in the older batch, 10 in the newer. Agency holds 5 of the newer.</summary>
    private async Task SeedAsync()
    {
        await using var db = _fixture.CreateContext(_companyId);

        db.InventoryOwners.AddRange(
            Owner(_vanOwnerId, InventoryOwnerType.SalesRep, _repId, "Ruwan's van"),
            Owner(_agencyOwnerId, InventoryOwnerType.Agency, _agencyId, "Colombo agency"));

        db.StockItems.AddRange(
            Stock(_vanOwnerId, _olderBatch, 20),
            Stock(_vanOwnerId, _newerBatch, 10),
            Stock(_agencyOwnerId, _newerBatch, 5));

        await db.SaveChangesAsync();
    }

    private InventoryOwner Owner(Guid id, InventoryOwnerType type, Guid externalId, string name) => new()
    {
        InventoryOwnerId = id, CompanyId = _companyId, OwnerType = type, ExternalOwnerId = externalId,
        DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private StockItem Stock(Guid ownerId, Guid batchId, int quantity)
    {
        var item = new StockItem
        {
            StockItemId = Guid.NewGuid(), CompanyId = _companyId, InventoryOwnerId = ownerId,
            ProductId = _productId, BatchId = batchId
        };
        item.ApplyMovement(new StockMovement
        {
            StockMovementId = Guid.NewGuid(), StockItemId = item.StockItemId,
            MovementType = StockMovementType.Adjustment, OnHandDelta = quantity, ReservedDelta = 0,
            ActorId = "test", Reason = "Seed", OccurredAt = DateTimeOffset.UtcNow
        });
        return item;
    }

    private VanStockReturnedEvent Event(int accepted, Guid? eventId = null, Guid? vanOwnerId = null, Guid? repId = null) => new(
        eventId ?? Guid.NewGuid(), "VanStockReturned", "1.0", _companyId, Guid.NewGuid(),
        "VR-260925-K7MQ4R", repId ?? _repId, _agencyId, vanOwnerId ?? _vanOwnerId,
        new[] { new VanStockReturnedLine(_productId, accepted) },
        DateTimeOffset.UtcNow, "van-transfer-test");

    private async Task HandleAsync(VanStockReturnedEvent e)
    {
        await using var db = _fixture.CreateContext(_companyId);
        var reservations = new StockReservationService(
            db, new FixedTenant(_companyId), Options.Create(new StockReservationOptions { TtlMinutes = 15 }),
            new LowStockDetectionService(db));
        var handler = new OrderEventHandler(reservations, new NoOpSystemTenant(), db, new LowStockDetectionService(db));
        await handler.HandleAsync(e);
    }

    private async Task<Dictionary<(Guid Owner, Guid? Batch), int>> OnHandAsync()
    {
        await using var db = _fixture.CreateContext(_companyId);
        return await db.StockItems.AsNoTracking()
            .Where(item => item.ProductId == _productId)
            .ToDictionaryAsync(item => (item.InventoryOwnerId, item.BatchId), item => item.QuantityOnHand);
    }

    // Scenario 3 on the Inventory side: 10 counted of 12 declared → 10 move.
    [Fact]
    public async Task The_accepted_quantity_moves_from_the_van_to_the_agency_oldest_batch_first()
    {
        await SeedAsync();

        await HandleAsync(Event(accepted: 10));

        var stock = await OnHandAsync();
        Assert.Equal(10, stock[(_vanOwnerId, _olderBatch)]);
        Assert.Equal(10, stock[(_vanOwnerId, _newerBatch)]);
        Assert.Equal(10, stock[(_agencyOwnerId, _olderBatch)]); // row created, same batch
        Assert.Equal(5, stock[(_agencyOwnerId, _newerBatch)]);

        await using var db = _fixture.CreateContext(_companyId);
        var movements = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.ReferenceType == "VanReturn" && movement.ReferenceId == "VR-260925-K7MQ4R")
            .ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, movement => Assert.Equal(StockMovementType.Transferred, movement.MovementType));
        Assert.Equal(0, movements.Sum(movement => movement.OnHandDelta));
    }

    // Testing summary: van stock reaches zero after a full accepted return.
    [Fact]
    public async Task A_full_return_empties_the_van_across_batches()
    {
        await SeedAsync();

        await HandleAsync(Event(accepted: 30));

        var stock = await OnHandAsync();
        Assert.Equal(0, stock[(_vanOwnerId, _olderBatch)]);
        Assert.Equal(0, stock[(_vanOwnerId, _newerBatch)]);
        Assert.Equal(20, stock[(_agencyOwnerId, _olderBatch)]);
        Assert.Equal(15, stock[(_agencyOwnerId, _newerBatch)]);
    }

    [Fact]
    public async Task Replaying_the_same_event_moves_nothing_more()
    {
        await SeedAsync();
        var e = Event(accepted: 10);

        await HandleAsync(e);
        await HandleAsync(e);

        Assert.Equal(20, (await OnHandAsync())[(_vanOwnerId, _olderBatch)] + (await OnHandAsync())[(_vanOwnerId, _newerBatch)]);
    }

    [Fact]
    public async Task More_than_the_van_holds_is_refused_and_nothing_moves()
    {
        await SeedAsync();

        var error = await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(Event(accepted: 31)));

        Assert.Contains("holds only 30", error.Message);
        var stock = await OnHandAsync();
        Assert.Equal(20, stock[(_vanOwnerId, _olderBatch)]);
        Assert.Equal(5, stock[(_agencyOwnerId, _newerBatch)]);
        Assert.False(stock.ContainsKey((_agencyOwnerId, _olderBatch)));
    }

    [Fact]
    public async Task Stock_held_for_a_cash_sale_is_not_returned()
    {
        await SeedAsync();
        await using (var db = _fixture.CreateContext(_companyId))
        {
            var reservations = new StockReservationService(
                db, new FixedTenant(_companyId), Options.Create(new StockReservationOptions { TtlMinutes = 15 }),
                new LowStockDetectionService(db));
            var held = await reservations.ReserveAsync(new ReserveStockRequest(
                "ORD-260925-HELD01", _vanOwnerId, new[] { new ReservationLineRequest(_productId, null, 25) }));
            Assert.Equal(ReservationOutcome.Success, held.Outcome);
        }

        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(Event(accepted: 10)));
    }

    [Fact]
    public async Task A_van_owner_that_is_not_the_reps_is_refused()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(Event(accepted: 1, repId: Guid.NewGuid())));
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() => HandleAsync(Event(accepted: 1, vanOwnerId: _agencyOwnerId)));
    }

    private sealed class FixedTenant(Guid companyId) : ITenantContext
    {
        public Guid? CompanyId { get; } = companyId;
    }

    private sealed class NoOpSystemTenant : ISystemTenantContext
    {
        public IDisposable BeginSystemTenantScope(Guid companyId) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
