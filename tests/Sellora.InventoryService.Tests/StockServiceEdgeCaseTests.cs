using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

/// <summary>Guard clauses and race outcomes of the stock services.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class StockServiceEdgeCaseTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly InventoryOwner _owner;
    private readonly StockItem _stock;

    public StockServiceEdgeCaseTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _owner = TestData.Owner(_companyId, InventoryOwnerType.Agency);
        _stock = TestData.Stock(_owner, 10, batchId: Guid.NewGuid());
    }

    private async Task SeedAsync(params InventoryOwner[] extraOwners)
    {
        await using var db = _fixture.CreateContext(_companyId);
        db.InventoryOwners.Add(_owner);
        db.InventoryOwners.AddRange(extraOwners);
        db.StockItems.Add(_stock);
        await db.SaveChangesAsync();
    }

    private static StockReservationService ReservationsFor(InventoryDbContext db, Guid? companyId) => new(
        db, new FixedTenantContext(companyId), Options.Create(new StockReservationOptions()),
        new LowStockDetectionService(db));

    private StockReservationService Reservations(InventoryDbContext db) => ReservationsFor(db, _companyId);

    private ReserveStockRequest Request(string reference, int quantity, Guid? batchId = null) => new(
        reference, _owner.InventoryOwnerId,
        new[] { new ReservationLineRequest(_stock.ProductId, batchId, quantity) });

    private async Task TamperReservedAsync(Guid stockItemId, int quantityReserved)
    {
        await using var db = _fixture.CreateContext(_companyId);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE stock_item SET quantity_reserved = {quantityReserved} WHERE stock_item_id = {stockItemId}");
    }

    [Fact]
    public async Task Reservation_calls_without_a_tenant_are_refused()
    {
        await using var db = _fixture.CreateContext(null);
        var service = ReservationsFor(db, null);

        Assert.Empty(await service.CheckAvailabilityAsync(new CheckAvailabilityRequest(Guid.NewGuid(),
            new[] { new ReservationLineRequest(Guid.NewGuid(), null, 1) })));
        Assert.Equal(ReservationOutcome.TenantNotAvailable, (await service.ReserveAsync(Request("ORD-1", 1))).Outcome);
        Assert.Equal(ReservationOutcome.TenantNotAvailable, (await service.ReleaseAsync(Guid.NewGuid())).Outcome);
        Assert.Equal(ReservationOutcome.TenantNotAvailable, (await service.CancelForOrderAsync(Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Invalid_reservation_requests_explain_what_is_missing()
    {
        await using var db = _fixture.CreateContext(_companyId);
        var service = Reservations(db);
        var product = Guid.NewGuid();

        var results = new[]
        {
            await service.ReserveAsync(new ReserveStockRequest(" ", Guid.NewGuid(), new[] { new ReservationLineRequest(product, null, 1) })),
            await service.ReserveAsync(new ReserveStockRequest("ORD-1", Guid.Empty, new[] { new ReservationLineRequest(product, null, 1) })),
            await service.ReserveAsync(new ReserveStockRequest("ORD-1", Guid.NewGuid(), Array.Empty<ReservationLineRequest>())),
            await service.ReserveAsync(new ReserveStockRequest("ORD-1", Guid.NewGuid(), new[] { new ReservationLineRequest(product, null, 0) })),
        };

        Assert.All(results, result => Assert.Equal(ReservationOutcome.InvalidRequest, result.Outcome));
        Assert.Equal(4, results.Select(result => result.Message).Distinct().Count());
    }

    [Fact]
    public async Task A_named_batch_reserves_only_from_that_batch()
    {
        await SeedAsync();
        await using var db = _fixture.CreateContext(_companyId);

        var wrongBatch = await Reservations(db).ReserveAsync(Request("ORD-BATCH-1", 1, Guid.NewGuid()));
        var rightBatch = await Reservations(db).ReserveAsync(Request("ORD-BATCH-2", 4, _stock.BatchId));

        Assert.Equal(ReservationOutcome.InsufficientStock, wrongBatch.Outcome);
        Assert.Equal(ReservationOutcome.Success, rightBatch.Outcome);
        Assert.Equal(_stock.BatchId, Assert.Single(rightBatch.Reservation!.Lines).BatchId);
    }

    [Fact]
    public async Task Cancelling_an_unknown_reservation_reports_not_found()
    {
        await using var db = _fixture.CreateContext(_companyId);

        var result = await Reservations(db).CancelForOrderAsync(Guid.NewGuid());

        Assert.Equal(ReservationOutcome.ReservationNotFound, result.Outcome);
    }

    [Fact]
    public async Task Completing_a_reservation_whose_hold_disappeared_is_rolled_back()
    {
        await SeedAsync();
        Guid reservationId;
        await using (var db = _fixture.CreateContext(_companyId))
        {
            reservationId = (await Reservations(db).ReserveAsync(Request("ORD-TAMPERED-1", 3))).Reservation!.ReservationId;
        }

        await TamperReservedAsync(_stock.StockItemId, 0);

        await using (var db = _fixture.CreateContext(_companyId))
        {
            var confirmed = await Reservations(db).ConfirmAsync(reservationId);
            Assert.Equal(ReservationOutcome.InsufficientStock, confirmed.Outcome);
        }

        await using var check = _fixture.CreateContext(_companyId);
        var reservation = await check.StockReservations.AsNoTracking().SingleAsync(r => r.ReservationId == reservationId);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(10, (await check.StockItems.AsNoTracking().SingleAsync(i => i.StockItemId == _stock.StockItemId)).QuantityOnHand);
    }

    private sealed class BeforeFirstSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await action();
            }

            return result;
        }
    }

    private InventoryDbContext ContextWith(SaveChangesInterceptor interceptor) => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options,
        new FixedTenantContext(_companyId));

    private StockAdjustmentService Adjustments(InventoryDbContext db) => new(
        db, new FixedTenantContext(_companyId), new FakeCurrentUser("CompanyAdmin"));

    private async Task<AdjustStockResult> AdjustInParallelContextAsync(Guid productId, int delta)
    {
        await using var other = _fixture.CreateContext(_companyId);
        return await Adjustments(other).AdjustAsync(
            new AdjustStockRequest(_owner.InventoryOwnerId, productId, null, delta, "Concurrent count"));
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_stock_row_is_a_conflict()
    {
        await SeedAsync();
        var productId = Guid.NewGuid();
        AdjustStockResult? winner = null;

        await using var db = ContextWith(new BeforeFirstSaveInterceptor(async () =>
            winner = await AdjustInParallelContextAsync(productId, 3)));

        var loser = await Adjustments(db).AdjustAsync(
            new AdjustStockRequest(_owner.InventoryOwnerId, productId, null, 5, "Count"));

        Assert.Equal(AdjustStockOutcome.Success, winner!.Outcome);
        Assert.Equal(AdjustStockOutcome.ConcurrencyConflict, loser.Outcome);
    }

    [Fact]
    public async Task A_stale_stock_row_version_is_a_conflict()
    {
        var productId = Guid.NewGuid();
        await SeedAsync();
        Assert.Equal(AdjustStockOutcome.Success, (await AdjustInParallelContextAsync(productId, 4)).Outcome);

        await using var db = ContextWith(new BeforeFirstSaveInterceptor(async () =>
            await AdjustInParallelContextAsync(productId, 1)));

        var stale = await Adjustments(db).AdjustAsync(
            new AdjustStockRequest(_owner.InventoryOwnerId, productId, null, 2, "Count"));

        Assert.Equal(AdjustStockOutcome.ConcurrencyConflict, stale.Outcome);
    }

    [Fact]
    public async Task A_new_stock_row_without_movement_is_allowed()
    {
        await SeedAsync();
        await using var db = _fixture.CreateContext(_companyId);

        db.StockItems.Add(new StockItem
        {
            StockItemId = Guid.NewGuid(), CompanyId = _companyId,
            InventoryOwnerId = _owner.InventoryOwnerId, ProductId = Guid.NewGuid()
        });

        Assert.Equal(1, await db.SaveChangesAsync());
    }

    [Fact]
    public async Task Fulfilment_rejects_missing_tenant_agency_and_company_owners()
    {
        var shortAgency = TestData.Owner(_companyId, InventoryOwnerType.Agency);
        await SeedAsync(shortAgency);
        await using var db = _fixture.CreateContext(_companyId);
        var lines = new[] { new ReservationLineRequest(_stock.ProductId, null, 1) };

        FulfilmentResolver Resolver(Guid? companyId)
        {
            var tenant = new FixedTenantContext(companyId);
            return new FulfilmentResolver(new FulfilmentOwnerLookup(db, tenant), ReservationsFor(db, companyId),
                tenant, new RecordingFulfilmentDecisionLogger());
        }

        var noTenant = await Resolver(null).ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest("ORD-F-1", shortAgency.ExternalOwnerId, lines));
        var unknownAgency = await Resolver(_companyId).ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest("ORD-F-2", Guid.NewGuid(), lines));
        // The agency holds nothing and this company has no company-stock owner.
        var noCompanyOwner = await Resolver(_companyId).ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest("ORD-F-3", shortAgency.ExternalOwnerId, lines));

        Assert.Equal(ReservationOutcome.TenantNotAvailable, noTenant.Outcome);
        Assert.Equal(ReservationOutcome.InventoryOwnerNotFound, unknownAgency.Outcome);
        Assert.Equal(ReservationOutcome.InventoryOwnerNotFound, noCompanyOwner.Outcome);
        Assert.Null(await new FulfilmentOwnerLookup(db, new FixedTenantContext(null)).FindCompanyOwnerAsync());
    }

    [Fact]
    public async Task Stock_reads_without_a_tenant_return_nothing()
    {
        await using var db = _fixture.CreateContext(null);

        var stock = await new StockReadService(db, new FixedTenantContext(null), new FakeCurrentUser("CompanyAdmin"))
            .GetStockAsync(new StockListQuery());

        Assert.Empty(stock);
    }
}
