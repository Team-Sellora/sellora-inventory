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
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public override int SaveChanges()
    {
        EnsureStockMovementsAreAppendOnly();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureStockMovementsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStockMovementsAreAppendOnly();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
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
}