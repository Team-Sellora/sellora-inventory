using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Api.Tenancy;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.HierarchyEvents;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Persistence.Seeding;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

/// <summary>Hierarchy projection, staging seed, expiry sweeper and ledger guards.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PersistenceTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();

    public PersistenceTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private AgencyRegisteredEvent AgencyRegistered(Guid agencyId, string name) => new(
        Guid.NewGuid(), "AgencyRegistered", "1.0", _companyId, agencyId, agencyId, name,
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "hierarchy-test");

    private SalesRepAssignedEvent SalesRepAssigned(Guid repId, Guid agencyId) => new(
        Guid.NewGuid(), "SalesRepAssigned", "1.0", _companyId, repId, repId, "Ruwan Dias",
        Guid.NewGuid(), agencyId, DateTimeOffset.UtcNow, "hierarchy-test");

    private async Task HandleAsync(Func<InventoryOwnerHierarchyEventHandler, Task> handle)
    {
        await using var db = _fixture.CreateContext(_companyId);
        await handle(new InventoryOwnerHierarchyEventHandler(db));
    }

    [Fact]
    public async Task Hierarchy_events_project_company_agency_and_rep_owners()
    {
        var agencyId = Guid.NewGuid();
        var repId = Guid.NewGuid();

        // The rep can arrive before their agency: a placeholder is created...
        await HandleAsync(handler => handler.HandleAsync(SalesRepAssigned(repId, agencyId)));
        // ...and AgencyRegistered later gives it the real name.
        await HandleAsync(handler => handler.HandleAsync(AgencyRegistered(agencyId, "Colombo Agency")));
        await HandleAsync(handler => handler.HandleAsync(SalesRepAssigned(repId, agencyId)));

        await using var db = _fixture.CreateContext(_companyId);
        var owners = (await db.InventoryOwners.AsNoTracking().ToListAsync())
            .OrderBy(owner => owner.OwnerType)
            .ToList();

        Assert.Collection(owners,
            company => Assert.Equal((InventoryOwnerType.Company, _companyId, "Company Stock"),
                (company.OwnerType, company.ExternalOwnerId, company.DisplayName)),
            agency => Assert.Equal((InventoryOwnerType.Agency, agencyId, "Colombo Agency"),
                (agency.OwnerType, agency.ExternalOwnerId, agency.DisplayName)),
            rep => Assert.Equal((InventoryOwnerType.SalesRep, repId, "Ruwan Dias"),
                (rep.OwnerType, rep.ExternalOwnerId, rep.DisplayName)));
    }

    [Fact]
    public async Task Hierarchy_events_reactivate_an_inactive_owner()
    {
        var agencyId = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext(_companyId))
        {
            seed.InventoryOwners.Add(TestData.Owner(_companyId, InventoryOwnerType.Agency, agencyId, "Old name", isActive: false));
            await seed.SaveChangesAsync();
        }

        await HandleAsync(handler => handler.HandleAsync(AgencyRegistered(agencyId, "New name")));

        await using var db = _fixture.CreateContext(_companyId);
        var agency = await db.InventoryOwners.SingleAsync(owner => owner.ExternalOwnerId == agencyId);
        Assert.True(agency.IsActive);
        Assert.Equal("New name", agency.DisplayName);
    }

    [Fact]
    public async Task Staging_seed_is_idempotent()
    {
        var seedCompany = Guid.Parse("30000000-0000-0000-0000-000000000001");

        await using (var db = _fixture.CreateContext(seedCompany))
        {
            await DevelopmentInventorySeeder.SeedAsync(db);
        }

        await using (var db = _fixture.CreateContext(seedCompany))
        {
            await DevelopmentInventorySeeder.SeedAsync(db);
        }

        await using var check = _fixture.CreateContext(seedCompany);
        Assert.Equal(5, await check.InventoryOwners.CountAsync());
        Assert.Equal(6, await check.StockItems.CountAsync());
        Assert.Equal(500, (await check.StockItems.SingleAsync(item =>
            item.StockItemId == Guid.Parse("70000000-0000-0000-0000-000000000001"))).QuantityOnHand);
    }

    [Fact]
    public async Task Expired_reservations_are_released_by_the_sweeper()
    {
        var owner = TestData.Owner(_companyId, InventoryOwnerType.Company, _companyId);
        var stock = TestData.Stock(owner, 5);
        var stuckStock = TestData.Stock(owner, 5);
        Guid reservationId;

        await using (var db = _fixture.CreateContext(_companyId))
        {
            db.InventoryOwners.Add(owner);
            db.StockItems.AddRange(stock, stuckStock);
            await db.SaveChangesAsync();

            var service = new StockReservationService(db, new FixedTenantContext(_companyId),
                Options.Create(new StockReservationOptions()), new LowStockDetectionService(db));
            reservationId = (await service.ReserveAsync(new ReserveStockRequest("ORD-EXPIRES-1",
                owner.InventoryOwnerId, new[] { new ReservationLineRequest(stock.ProductId, null, 2) })))
                .Reservation!.ReservationId;
            var stuckId = (await service.ReserveAsync(new ReserveStockRequest("ORD-EXPIRES-STUCK",
                owner.InventoryOwnerId, new[] { new ReservationLineRequest(stuckStock.ProductId, null, 2) })))
                .Reservation!.ReservationId;

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stock_reservation SET expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE reservation_id IN ({reservationId}, {stuckId})");
            // The stuck reservation's hold vanished, so the sweeper can only log it.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stock_item SET quantity_reserved = 0 WHERE stock_item_id = {stuckStock.StockItemId}");
        }

        var services = new ServiceCollection()
            .AddDbContext<InventoryDbContext>(options => options.UseNpgsql(_fixture.ConnectionString))
            .AddHttpContextAccessor()
            .AddScoped<HttpTenantContext>()
            .AddScoped<ITenantContext>(provider => provider.GetRequiredService<HttpTenantContext>())
            .AddScoped<ISystemTenantContext>(provider => provider.GetRequiredService<HttpTenantContext>())
            .AddScoped<LowStockDetectionService>()
            .AddScoped<IStockReservationService, StockReservationService>()
            .AddSingleton(Options.Create(new StockReservationOptions()))
            .BuildServiceProvider();

        using var sweeper = new ReservationExpirySweeper(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StockReservationOptions { SweepIntervalSeconds = 1 }),
            NullLogger<ReservationExpirySweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            ReservationStatus status;
            do
            {
                await Task.Delay(500);
                await using var db = _fixture.CreateContext(_companyId);
                status = (await db.StockReservations.AsNoTracking()
                    .SingleAsync(reservation => reservation.ReservationId == reservationId)).Status;
            }
            while (status == ReservationStatus.Active && DateTime.UtcNow < deadline);

            Assert.Equal(ReservationStatus.Released, status);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Balance_changes_need_ledger_entries_and_the_ledger_is_append_only()
    {
        var owner = TestData.Owner(_companyId, InventoryOwnerType.Company, _companyId);
        var stock = TestData.Stock(owner, 5);

        await using (var db = _fixture.CreateContext(_companyId))
        {
            db.InventoryOwners.Add(owner);
            db.StockItems.Add(stock);
            Assert.True(db.SaveChanges() > 0);
        }

        await using (var db = _fixture.CreateContext(_companyId))
        {
            var item = await db.StockItems.SingleAsync(candidate => candidate.StockItemId == stock.StockItemId);
            db.Entry(item).Property(candidate => candidate.QuantityOnHand).CurrentValue = 99;

            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = _fixture.CreateContext(_companyId))
        {
            var movement = await db.StockMovements.SingleAsync(candidate => candidate.StockItemId == stock.StockItemId);
            movement.Reason = "Rewritten history";

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = _fixture.CreateContext(_companyId))
        {
            var movement = await db.StockMovements.SingleAsync(candidate => candidate.StockItemId == stock.StockItemId);
            db.StockMovements.Remove(movement);

            Assert.Throws<InvalidOperationException>(() => db.SaveChanges(acceptAllChangesOnSuccess: true));
        }
    }
}
