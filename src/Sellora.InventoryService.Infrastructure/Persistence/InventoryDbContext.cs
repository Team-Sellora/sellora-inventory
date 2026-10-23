using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Infrastructure.Persistence;

public class InventoryDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public InventoryDbContext(
        DbContextOptions<InventoryDbContext> options,
        ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<InventoryOwner> InventoryOwners => Set<InventoryOwner>();
    public DbSet<ProcessedInventoryEvent> ProcessedInventoryEvents => Set<ProcessedInventoryEvent>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public override int SaveChanges()
    {
        EnforceStockBalanceLedgerConsistency();
        EnsureStockMovementsAreAppendOnly();

        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceStockBalanceLedgerConsistency();
        EnsureStockMovementsAreAppendOnly();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        EnforceStockBalanceLedgerConsistency();
        EnsureStockMovementsAreAppendOnly();

        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceStockBalanceLedgerConsistency();
        EnsureStockMovementsAreAppendOnly();

        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(InventoryDbContext).Assembly);

        modelBuilder.Entity<ProcessedInventoryEvent>()
            .HasQueryFilter(e => _tenantContext.CompanyId != null &&
                e.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<OutboxMessage>()
            .HasQueryFilter(e => _tenantContext.CompanyId != null && e.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<InventoryOwner>()
            .HasQueryFilter(owner =>
                _tenantContext.CompanyId != null &&
                owner.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<StockItem>()
            .HasQueryFilter(stockItem =>
                _tenantContext.CompanyId != null &&
                stockItem.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<StockMovement>()
            .HasQueryFilter(movement =>
                _tenantContext.CompanyId != null &&
                movement.StockItem.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<StockReservation>()
            .HasQueryFilter(reservation =>
                _tenantContext.CompanyId != null &&
                reservation.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<StockReservationLine>()
            .HasQueryFilter(line =>
                _tenantContext.CompanyId != null &&
                line.Reservation.CompanyId == _tenantContext.CompanyId);
    }

    private void EnforceStockBalanceLedgerConsistency()
    {
        var addedMovements = ChangeTracker
            .Entries<StockMovement>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();

        var changedStockItems = ChangeTracker
            .Entries<StockItem>()
            .Where(entry =>
                entry.State == EntityState.Added ||
                (entry.State == EntityState.Modified &&
                 (entry.Property(item => item.QuantityOnHand).IsModified ||
                  entry.Property(item => item.QuantityReserved).IsModified)))
            .ToList();

        foreach (var stockItemEntry in changedStockItems)
        {
            var stockItem = stockItemEntry.Entity;

            var originalOnHand = stockItemEntry.State == EntityState.Added
                ? 0
                : stockItemEntry
                    .Property(item => item.QuantityOnHand)
                    .OriginalValue;

            var originalReserved = stockItemEntry.State == EntityState.Added
                ? 0
                : stockItemEntry
                    .Property(item => item.QuantityReserved)
                    .OriginalValue;

            var onHandDelta = stockItem.QuantityOnHand - originalOnHand;
            var reservedDelta = stockItem.QuantityReserved - originalReserved;

            if (onHandDelta == 0 && reservedDelta == 0)
            {
                continue;
            }

            var matchingMovements = addedMovements
                .Where(movement =>
                    movement.StockItemId == stockItem.StockItemId ||
                    ReferenceEquals(movement.StockItem, stockItem))
                .ToList();

            var recordedOnHandDelta = matchingMovements.Sum(
                movement => movement.OnHandDelta);

            var recordedReservedDelta = matchingMovements.Sum(
                movement => movement.ReservedDelta);

            if (recordedOnHandDelta != onHandDelta ||
                recordedReservedDelta != reservedDelta)
            {
                throw new InvalidOperationException(
                    "Every stock balance change must have matching stock movement ledger entries.");
            }
        }
    }

    private void EnsureStockMovementsAreAppendOnly()
    {
        var attemptedMutation = ChangeTracker
            .Entries<StockMovement>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);

        if (attemptedMutation)
        {
            throw new InvalidOperationException(
                "Stock movements are append-only and cannot be changed or deleted.");
        }
    }

    public DbSet<StockReservation> StockReservations =>
    Set<StockReservation>();

    public DbSet<StockReservationLine> StockReservationLines =>
        Set<StockReservationLine>();
}
