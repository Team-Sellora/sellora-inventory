using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class StockReservationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public StockReservationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReserveAsync_with_same_order_reference_is_idempotent()
    {
        var seed = await SeedStockAsync(10);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var request = Request(
            "ORDER-IDEMPOTENT-001",
            seed.OwnerId,
            seed.ProductIds[0],
            quantity: 3);

        var first = await service.ReserveAsync(request);
        var second = await service.ReserveAsync(request);

        Assert.Equal(ReservationOutcome.Success, first.Outcome);
        Assert.Equal(ReservationOutcome.Success, second.Outcome);
        Assert.NotNull(first.Reservation);
        Assert.NotNull(second.Reservation);
        Assert.Equal(
            first.Reservation!.ReservationId,
            second.Reservation!.ReservationId);

        var stockItem = await db.StockItems
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);

        Assert.Equal(10, stockItem.QuantityOnHand);
        Assert.Equal(3, stockItem.QuantityReserved);

        Assert.Equal(
            1,
            await db.StockReservations.CountAsync(
                reservation =>
                    reservation.OrderReference == "ORDER-IDEMPOTENT-001"));

        Assert.Equal(
            1,
            await db.StockMovements.CountAsync(
                movement =>
                    movement.ReservationId ==
                    first.Reservation.ReservationId));
    }

    [Fact]
    public async Task ReserveAsync_when_one_line_is_short_reserves_nothing()
    {
        var seed = await SeedStockAsync(5, 1);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var result = await service.ReserveAsync(
            new ReserveStockRequest(
                "ORDER-ALL-OR-NOTHING-001",
                seed.OwnerId,
                new[]
                {
                    new ReservationLineRequest(
                        seed.ProductIds[0],
                        null,
                        3),
                    new ReservationLineRequest(
                        seed.ProductIds[1],
                        null,
                        2)
                }));

        Assert.Equal(ReservationOutcome.InsufficientStock, result.Outcome);

        var shortage = Assert.Single(result.Shortages);
        Assert.Equal(seed.ProductIds[1], shortage.ProductId);
        Assert.Equal(2, shortage.RequestedQuantity);
        Assert.Equal(1, shortage.AvailableQuantity);

        var stockItems = await db.StockItems
            .OrderBy(item => item.ProductId)
            .ToListAsync();

        Assert.All(
            stockItems,
            stockItem => Assert.Equal(0, stockItem.QuantityReserved));

        Assert.Empty(await db.StockReservations.ToListAsync());

        Assert.Equal(
            2,
            await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task ReleaseAsync_releases_stock_and_writes_released_ledger_entry()
    {
        var seed = await SeedStockAsync(4);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var reserved = await service.ReserveAsync(
            Request(
                "ORDER-RELEASE-001",
                seed.OwnerId,
                seed.ProductIds[0],
                quantity: 3));

        Assert.Equal(ReservationOutcome.Success, reserved.Outcome);
        Assert.NotNull(reserved.Reservation);

        var released = await service.ReleaseAsync(
            reserved.Reservation!.ReservationId);

        Assert.Equal(ReservationOutcome.Success, released.Outcome);
        Assert.NotNull(released.Reservation);
        Assert.Equal("Released", released.Reservation!.Status);

        var stockItem = await db.StockItems
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);

        Assert.Equal(4, stockItem.QuantityOnHand);
        Assert.Equal(0, stockItem.QuantityReserved);
        Assert.Equal(4, stockItem.AvailableQuantity);

        var movement = await db.StockMovements
            .SingleAsync(candidate =>
                candidate.ReservationId ==
                reserved.Reservation.ReservationId &&
                candidate.MovementType == StockMovementType.Released);

        Assert.Equal(0, movement.OnHandDelta);
        Assert.Equal(-3, movement.ReservedDelta);
    }

    // US-E4-5: a scheduled delivery's stock is sold at placement, so a
    // rejection or shop cancellation must put it back on hand.
    [Fact]
    public async Task CancelForOrderAsync_returns_the_stock_of_a_confirmed_reservation()
    {
        var seed = await SeedStockAsync(10);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var reserved = await service.ReserveAsync(
            Request("ORDER-CANCEL-CONFIRMED-001", seed.OwnerId, seed.ProductIds[0], quantity: 4));
        var reservationId = reserved.Reservation!.ReservationId;
        Assert.Equal(ReservationOutcome.Success, (await service.ConfirmAsync(reservationId)).Outcome);

        var cancelled = await service.CancelForOrderAsync(reservationId);

        Assert.Equal(ReservationOutcome.Success, cancelled.Outcome);
        Assert.Equal("Released", cancelled.Reservation!.Status);

        var stockItem = await db.StockItems.AsNoTracking()
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);
        Assert.Equal(10, stockItem.QuantityOnHand);
        Assert.Equal(0, stockItem.QuantityReserved);

        var returned = await db.StockMovements.SingleAsync(movement =>
            movement.ReservationId == reservationId &&
            movement.MovementType == StockMovementType.Returned);
        Assert.Equal(4, returned.OnHandDelta);
        Assert.Equal(0, returned.ReservedDelta);
        Assert.Equal("ORDER-CANCEL-CONFIRMED-001", returned.ReferenceId);
    }

    [Fact]
    public async Task CancelForOrderAsync_on_a_held_reservation_is_an_ordinary_release()
    {
        var seed = await SeedStockAsync(6);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var reserved = await service.ReserveAsync(
            Request("ORDER-CANCEL-HELD-001", seed.OwnerId, seed.ProductIds[0], quantity: 2));

        var cancelled = await service.CancelForOrderAsync(reserved.Reservation!.ReservationId);

        Assert.Equal(ReservationOutcome.Success, cancelled.Outcome);
        var stockItem = await db.StockItems.AsNoTracking()
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);
        Assert.Equal(6, stockItem.QuantityOnHand);
        Assert.Equal(0, stockItem.QuantityReserved);
        Assert.True(await db.StockMovements.AnyAsync(movement =>
            movement.ReservationId == reserved.Reservation.ReservationId &&
            movement.MovementType == StockMovementType.Released));
    }

    [Fact]
    public async Task CancelForOrderAsync_twice_returns_the_stock_only_once()
    {
        var seed = await SeedStockAsync(10);

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        var reserved = await service.ReserveAsync(
            Request("ORDER-CANCEL-TWICE-001", seed.OwnerId, seed.ProductIds[0], quantity: 3));
        var reservationId = reserved.Reservation!.ReservationId;
        await service.ConfirmAsync(reservationId);

        await service.CancelForOrderAsync(reservationId);
        var replay = await service.CancelForOrderAsync(reservationId);

        // The event consumer treats this outcome as "already applied".
        Assert.Equal(ReservationOutcome.ReservationAlreadyReleased, replay.Outcome);
        var stockItem = await db.StockItems.AsNoTracking()
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);
        Assert.Equal(10, stockItem.QuantityOnHand);
        Assert.Equal(1, await db.StockMovements.CountAsync(movement =>
            movement.ReservationId == reservationId &&
            movement.MovementType == StockMovementType.Returned));
    }

    [Fact]
    public async Task ReserveAsync_parallel_requests_do_not_oversell()
    {
        var seed = await SeedStockAsync(1);

        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstAttempt = ReserveInSeparateContextAsync(
            seed,
            "ORDER-CONCURRENT-001",
            start.Task);

        var secondAttempt = ReserveInSeparateContextAsync(
            seed,
            "ORDER-CONCURRENT-002",
            start.Task);

        start.SetResult();

        var results = await Task.WhenAll(firstAttempt, secondAttempt);

        Assert.Equal(
            1,
            results.Count(
                result => result.Outcome == ReservationOutcome.Success));

        Assert.Equal(
            1,
            results.Count(
                result =>
                    result.Outcome == ReservationOutcome.InsufficientStock));

        await using var verificationDb =
            _fixture.CreateContext(seed.CompanyId);

        var stockItem = await verificationDb.StockItems
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);

        Assert.Equal(1, stockItem.QuantityOnHand);
        Assert.Equal(1, stockItem.QuantityReserved);
        Assert.Equal(0, stockItem.AvailableQuantity);

        Assert.Equal(
            1,
            await verificationDb.StockReservations.CountAsync(
                reservation =>
                    reservation.Status == ReservationStatus.Active));
    }

    private async Task<ReserveStockResult> ReserveInSeparateContextAsync(
        SeedData seed,
        string orderReference,
        Task startSignal)
    {
        await startSignal;

        await using var db = _fixture.CreateContext(seed.CompanyId);
        var service = CreateService(db, seed.CompanyId);

        return await service.ReserveAsync(
            Request(
                orderReference,
                seed.OwnerId,
                seed.ProductIds[0],
                quantity: 1));
    }

    [Theory]
    [InlineData(5, 10, false)]
    [InlineData(1, 10, true)]
    [InlineData(3, 10, false)]
    [InlineData(1, 3, true)]
    public async Task Resolved_source_is_persisted_and_confirmed_against_correct_owner(
        int agencyQuantity,
        int companyQuantity,
        bool expectCompany
    )
    {
        var seed = await SeedStockAsync(agencyQuantity);
        var companyOwnerId = Guid.NewGuid();
        var companyStockItemId = Guid.NewGuid();

        await using var db = _fixture.CreateContext(seed.CompanyId);

        var agencyId = await db.InventoryOwners
            .Where(owner => owner.InventoryOwnerId == seed.OwnerId)
            .Select(owner => owner.ExternalOwnerId)
            .SingleAsync();

        db.InventoryOwners.Add(new InventoryOwner
        {
            InventoryOwnerId = companyOwnerId,
            CompanyId = seed.CompanyId,
            OwnerType = InventoryOwnerType.Company,
            ExternalOwnerId = seed.CompanyId,
            DisplayName = "Test company",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var companyStock = new StockItem
        {
            StockItemId = companyStockItemId,
            CompanyId = seed.CompanyId,
            InventoryOwnerId = companyOwnerId,
            ProductId = seed.ProductIds[0]
        };

        companyStock.ApplyMovement(new StockMovement
        {
            StockMovementId = Guid.NewGuid(),
            StockItemId = companyStockItemId,
            MovementType = StockMovementType.Adjustment,
            OnHandDelta = companyQuantity,
            ReservedDelta = 0,
            ActorId = "test-user",
            Reason = "Seed company stock",
            OccurredAt = DateTimeOffset.UtcNow
        });

        db.StockItems.Add(companyStock);
        await db.SaveChangesAsync();

        var tenant = new TestTenantContext(seed.CompanyId);
        var resolver = new FulfilmentResolver(
            new FulfilmentOwnerLookup(db, tenant),
            CreateService(db, seed.CompanyId),
            tenant,
            new RecordingFulfilmentDecisionLogger()
        );

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                $"ORDER-SOURCE-{Guid.NewGuid():N}",
                agencyId,
                new[]
                {
                new ReservationLineRequest(seed.ProductIds[0], null, 3)
                }));

        Assert.Equal(ReservationOutcome.Success, result.Outcome);
        Assert.NotNull(result.Reservation);

        var reservationId = result.Reservation.ReservationId;
        var expectedOwnerId = expectCompany ? companyOwnerId : seed.OwnerId;
        var expectedStockId = expectCompany
            ? companyStockItemId
            : seed.StockItemIds[0];

        // A fresh context proves the source was persisted to the database.
        await using var confirmDb = _fixture.CreateContext(seed.CompanyId);

        var persisted = await confirmDb.StockReservations
            .AsNoTracking()
            .Include(reservation => reservation.Lines)
            .SingleAsync(reservation =>
                reservation.ReservationId == reservationId);

        Assert.Equal(expectedOwnerId, persisted.InventoryOwnerId);
        Assert.Equal(expectedOwnerId, result.Reservation.InventoryOwnerId);
        Assert.Equal(
            expectedStockId,
            Assert.Single(persisted.Lines).StockItemId);

        var confirmed = await CreateService(confirmDb, seed.CompanyId)
            .ConfirmAsync(reservationId);

        Assert.Equal(ReservationOutcome.Success, confirmed.Outcome);

        await using var verifyDb = _fixture.CreateContext(seed.CompanyId);

        var agencyStock = await verifyDb.StockItems
            .SingleAsync(item => item.StockItemId == seed.StockItemIds[0]);
        var remainingCompanyStock = await verifyDb.StockItems
            .SingleAsync(item => item.StockItemId == companyStockItemId);

        Assert.Equal(
            expectCompany ? agencyQuantity : agencyQuantity - 3,
            agencyStock.QuantityOnHand);
        Assert.Equal(
            expectCompany ? companyQuantity - 3 : companyQuantity,
            remainingCompanyStock.QuantityOnHand);
        Assert.Equal(0, agencyStock.QuantityReserved);
        Assert.Equal(0, remainingCompanyStock.QuantityReserved);

        var sold = Assert.Single(await verifyDb.StockMovements
            .Where(movement =>
                movement.ReservationId == reservationId &&
                movement.MovementType == StockMovementType.Sold)
            .ToListAsync());

        Assert.Equal(expectedStockId, sold.StockItemId);
        Assert.Equal(-3, sold.OnHandDelta);
        Assert.Equal(-3, sold.ReservedDelta);
        Assert.Equal(persisted.OrderReference, sold.ReferenceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Five_line_resolution_never_leaves_mixed_source_reservations(
    bool companyShort)
    {
        var agencyQuantities = new[] { 5, 5, 5, 5, 1 };
        var seed = await SeedStockAsync(agencyQuantities);
        var companyOwnerId = Guid.NewGuid();
        var orderReference = $"ORDER-FIVE-{Guid.NewGuid():N}";
        var companyStockIds = new List<Guid>();

        await using var db = _fixture.CreateContext(seed.CompanyId);

        var agencyId = await db.InventoryOwners
            .Where(owner => owner.InventoryOwnerId == seed.OwnerId)
            .Select(owner => owner.ExternalOwnerId)
            .SingleAsync();

        db.InventoryOwners.Add(new InventoryOwner
        {
            InventoryOwnerId = companyOwnerId,
            CompanyId = seed.CompanyId,
            OwnerType = InventoryOwnerType.Company,
            ExternalOwnerId = seed.CompanyId,
            DisplayName = "Test company",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        for (var index = 0; index < seed.ProductIds.Count; index++)
        {
            var stock = new StockItem
            {
                StockItemId = Guid.NewGuid(),
                CompanyId = seed.CompanyId,
                InventoryOwnerId = companyOwnerId,
                ProductId = seed.ProductIds[index]
            };

            stock.ApplyMovement(new StockMovement
            {
                StockMovementId = Guid.NewGuid(),
                StockItemId = stock.StockItemId,
                MovementType = StockMovementType.Adjustment,
                OnHandDelta = companyShort && index == 4 ? 1 : 5,
                ReservedDelta = 0,
                ActorId = "test-user",
                Reason = "Seed company basket stock",
                OccurredAt = DateTimeOffset.UtcNow
            });

            companyStockIds.Add(stock.StockItemId);
            db.StockItems.Add(stock);
        }

        await db.SaveChangesAsync();

        var tenant = new TestTenantContext(seed.CompanyId);
        var resolver = new FulfilmentResolver(
            new FulfilmentOwnerLookup(db, tenant),
            CreateService(db, seed.CompanyId),
            tenant,
            new RecordingFulfilmentDecisionLogger());

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                orderReference,
                agencyId,
                seed.ProductIds
                    .Select(productId =>
                        new ReservationLineRequest(productId, null, 3))
                    .ToArray()));

        await using var verifyDb = _fixture.CreateContext(seed.CompanyId);

        var stockItems = await verifyDb.StockItems
            .AsNoTracking()
            .ToListAsync();

        for (var index = 0; index < seed.ProductIds.Count; index++)
        {
            var agencyStock = stockItems.Single(item =>
                item.StockItemId == seed.StockItemIds[index]);
            var companyStock = stockItems.Single(item =>
                item.StockItemId == companyStockIds[index]);

            Assert.Equal(agencyQuantities[index], agencyStock.QuantityOnHand);
            Assert.Equal(0, agencyStock.QuantityReserved);

            Assert.Equal(
                companyShort && index == 4 ? 1 : 5,
                companyStock.QuantityOnHand);
            Assert.Equal(companyShort ? 0 : 3, companyStock.QuantityReserved);
        }

        var persistedReservations = await verifyDb.StockReservations
            .AsNoTracking()
            .Include(reservation => reservation.Lines)
            .Where(reservation => reservation.OrderReference == orderReference)
            .ToListAsync();

        var movements = await verifyDb.StockMovements
            .AsNoTracking()
            .Where(movement =>
                movement.ReferenceType == "Order" &&
                movement.ReferenceId == orderReference)
            .ToListAsync();

        if (companyShort)
        {
            Assert.Equal(ReservationOutcome.InsufficientStock, result.Outcome);
            Assert.Null(result.Reservation);
            Assert.Empty(persistedReservations);
            Assert.Empty(movements);

            var shortage = Assert.Single(result.Shortages);
            Assert.Equal(seed.ProductIds[4], shortage.ProductId);
            Assert.Equal(3, shortage.RequestedQuantity);
            Assert.Equal(1, shortage.AvailableQuantity);
        }
        else
        {
            Assert.Equal(ReservationOutcome.Success, result.Outcome);
            Assert.NotNull(result.Reservation);
            Assert.Equal(companyOwnerId, result.Reservation.InventoryOwnerId);

            var reservation = Assert.Single(persistedReservations);
            Assert.Equal(companyOwnerId, reservation.InventoryOwnerId);
            Assert.Equal(5, reservation.Lines.Count);

            Assert.All(reservation.Lines, line =>
            {
                Assert.Contains(line.StockItemId, companyStockIds);
                Assert.Equal(3, line.Quantity);
            });

            Assert.Equal(5, movements.Count);
            Assert.All(movements, movement =>
            {
                Assert.Equal(reservation.ReservationId, movement.ReservationId);
                Assert.Contains(movement.StockItemId, companyStockIds);
                Assert.Equal(StockMovementType.Reserved, movement.MovementType);
                Assert.Equal(0, movement.OnHandDelta);
                Assert.Equal(3, movement.ReservedDelta);
            });
        }
    }

    [Fact]
    public async Task Failed_second_line_does_not_leak_ledger_entries_into_retry()
    {
        var seed = await SeedStockAsync(5, 5);
        var interceptor = new FailSecondReservationUpdateInterceptor();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new InventoryDbContext(
            options, new TestTenantContext(seed.CompanyId));

        var service = CreateService(db, seed.CompanyId);
        var orderReference = $"ORDER-ROLLBACK-{Guid.NewGuid():N}";
        var request = new ReserveStockRequest(
            orderReference,
            seed.OwnerId,
            seed.ProductIds
                .Select(productId =>
                    new ReservationLineRequest(productId, null, 2))
                .ToArray());

        var failed = await service.ReserveAsync(request);

        Assert.True(interceptor.FailureInjected);
        Assert.Equal(ReservationOutcome.InsufficientStock, failed.Outcome);
        Assert.Null(failed.Reservation);

        Assert.DoesNotContain(
            db.ChangeTracker.Entries<StockMovement>(),
            entry => entry.State == EntityState.Added);

        await using (var afterFailure = _fixture.CreateContext(seed.CompanyId))
        {
            var stocks = await afterFailure.StockItems.ToListAsync();

            Assert.All(stocks, stock =>
            {
                Assert.Equal(5, stock.QuantityOnHand);
                Assert.Equal(0, stock.QuantityReserved);
            });

            Assert.False(await afterFailure.StockReservations.AnyAsync(
                reservation => reservation.OrderReference == orderReference));

            Assert.False(await afterFailure.StockMovements.AnyAsync(
                movement =>
                    movement.ReferenceType == "Order" &&
                    movement.ReferenceId == orderReference));
        }

        // Reuse the same context to expose leftover tracked ledger entries.
        var retry = await service.ReserveAsync(request);

        Assert.Equal(ReservationOutcome.Success, retry.Outcome);
        Assert.NotNull(retry.Reservation);

        await using var verifyDb = _fixture.CreateContext(seed.CompanyId);

        var finalStocks = await verifyDb.StockItems.ToListAsync();
        Assert.All(finalStocks, stock =>
        {
            Assert.Equal(5, stock.QuantityOnHand);
            Assert.Equal(2, stock.QuantityReserved);
        });

        var reservation = Assert.Single(
            await verifyDb.StockReservations
                .Where(candidate => candidate.OrderReference == orderReference)
                .ToListAsync());

        Assert.Equal(retry.Reservation.ReservationId, reservation.ReservationId);

        var movements = await verifyDb.StockMovements
            .Where(movement =>
                movement.ReferenceType == "Order" &&
                movement.ReferenceId == orderReference)
            .ToListAsync();

        Assert.Equal(2, movements.Count);
        Assert.Equal(
            2,
            movements.Select(movement => movement.StockItemId).Distinct().Count());

        Assert.All(movements, movement =>
        {
            Assert.Equal(reservation.ReservationId, movement.ReservationId);
            Assert.Equal(StockMovementType.Reserved, movement.MovementType);
            Assert.Equal(0, movement.OnHandDelta);
            Assert.Equal(2, movement.ReservedDelta);
        });
    }

    private async Task<SeedData> SeedStockAsync(
        params int[] quantities)
    {
        var companyId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await using var db = _fixture.CreateContext(companyId);

        db.InventoryOwners.Add(new InventoryOwner
        {
            InventoryOwnerId = ownerId,
            CompanyId = companyId,
            OwnerType = InventoryOwnerType.Agency,
            ExternalOwnerId = Guid.NewGuid(),
            DisplayName = "Test agency",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var productIds = new List<Guid>();
        var stockItemIds = new List<Guid>();

        foreach (var quantity in quantities)
        {
            var productId = Guid.NewGuid();

            var stockItem = new StockItem
            {
                StockItemId = Guid.NewGuid(),
                CompanyId = companyId,
                InventoryOwnerId = ownerId,
                ProductId = productId
            };

            stockItem.ApplyMovement(new StockMovement
            {
                StockMovementId = Guid.NewGuid(),
                StockItemId = stockItem.StockItemId,
                MovementType = StockMovementType.Adjustment,
                OnHandDelta = quantity,
                ReservedDelta = 0,
                ActorId = "test-user",
                Reason = "Seed test stock",
                OccurredAt = DateTimeOffset.UtcNow
            });

            db.StockItems.Add(stockItem);

            productIds.Add(productId);
            stockItemIds.Add(stockItem.StockItemId);
        }

        await db.SaveChangesAsync();

        return new SeedData(
            companyId,
            ownerId,
            productIds,
            stockItemIds);
    }

    private static StockReservationService CreateService(
        InventoryDbContext db,
        Guid companyId) =>
        new(
            db,
            new TestTenantContext(companyId),
            Options.Create(new StockReservationOptions
            {
                TtlMinutes = 15
            }),
            new LowStockDetectionService(db));

    private static ReserveStockRequest Request(
        string orderReference,
        Guid ownerId,
        Guid productId,
        int quantity) =>
        new(
            orderReference,
            ownerId,
            new[]
            {
                new ReservationLineRequest(productId, null, quantity)
            });

    private sealed record SeedData(
        Guid CompanyId,
        Guid OwnerId,
        IReadOnlyList<Guid> ProductIds,
        IReadOnlyList<Guid> StockItemIds);

    private sealed class FailSecondReservationUpdateInterceptor
    : DbCommandInterceptor
    {
        private int _reservationUpdates;

        public bool FailureInjected { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                "quantity_reserved = quantity_reserved +",
                StringComparison.Ordinal))
            {
                _reservationUpdates++;

                if (_reservationUpdates == 2)
                {
                    FailureInjected = true;

                    // Simulate a conditional UPDATE affecting zero rows.
                    return ValueTask.FromResult(
                        InterceptionResult<int>.SuppressWithResult(0));
                }
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public TestTenantContext(Guid companyId)
        {
            CompanyId = companyId;
        }

        public Guid? CompanyId { get; }
    }
}
