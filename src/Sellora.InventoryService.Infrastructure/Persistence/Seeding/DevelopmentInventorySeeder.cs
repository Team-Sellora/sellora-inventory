using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;

namespace Sellora.InventoryService.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates predictable inventory for the committed Organization and Catalog
/// staging data. This is idempotent and must only run in Staging.
/// </summary>
public static class DevelopmentInventorySeeder
{
    private static readonly Guid CompanyId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset SeededAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task SeedAsync(
        InventoryDbContext db,
        CancellationToken cancellationToken = default)
    {
        const string seedReference = "SELLORA-STAGING-INVENTORY-V1";

        var alreadySeeded = await db.StockMovements
            .IgnoreQueryFilters()
            .AnyAsync(movement => movement.ReferenceId == seedReference,
                cancellationToken);

        if (alreadySeeded)
        {
            return;
        }

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        var companyOwner = CreateOwner(
            "60000000-0000-0000-0000-000000000001",
            InventoryOwnerType.Company,
            CompanyId,
            "Company Stock");
        var colomboAgencyOwner = CreateOwner(
            "60000000-0000-0000-0000-000000000100",
            InventoryOwnerType.Agency,
            Guid.Parse("30000000-0000-0000-0000-000000000100"),
            "Colombo Distribution Agency");
        var kandyAgencyOwner = CreateOwner(
            "60000000-0000-0000-0000-000000000200",
            InventoryOwnerType.Agency,
            Guid.Parse("30000000-0000-0000-0000-000000000200"),
            "Kandy Distribution Agency");
        var colomboSalesRepOwner = CreateOwner(
            "60000000-0000-0000-0000-000000001001",
            InventoryOwnerType.SalesRep,
            Guid.Parse("30000000-0000-0000-0000-000000001001"),
            "Ruwan Dias");
        var kandySalesRepOwner = CreateOwner(
            "60000000-0000-0000-0000-000000002001",
            InventoryOwnerType.SalesRep,
            Guid.Parse("30000000-0000-0000-0000-000000002001"),
            "Ishara Kumari");

        db.InventoryOwners.AddRange(
            companyOwner,
            colomboAgencyOwner,
            kandyAgencyOwner,
            colomboSalesRepOwner,
            kandySalesRepOwner);

        db.StockItems.AddRange(
            CreateStockItem("70000000-0000-0000-0000-000000000001", companyOwner, "40000000-0000-0000-0000-000000000001", 500, seedReference),
            CreateStockItem("70000000-0000-0000-0000-000000000002", colomboAgencyOwner, "40000000-0000-0000-0000-000000000002", 200, seedReference),
            CreateStockItem("70000000-0000-0000-0000-000000000003", colomboAgencyOwner, "40000000-0000-0000-0000-000000000003", 150, seedReference),
            CreateStockItem("70000000-0000-0000-0000-000000000004", kandyAgencyOwner, "40000000-0000-0000-0000-000000000005", 100, seedReference),
            CreateStockItem("70000000-0000-0000-0000-000000000005", colomboSalesRepOwner, "40000000-0000-0000-0000-000000000004", 40, seedReference),
            CreateStockItem("70000000-0000-0000-0000-000000000006", kandySalesRepOwner, "40000000-0000-0000-0000-000000000001", 30, seedReference));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static InventoryOwner CreateOwner(
        string inventoryOwnerId,
        InventoryOwnerType ownerType,
        Guid externalOwnerId,
        string displayName) =>
        new()
        {
            InventoryOwnerId = Guid.Parse(inventoryOwnerId),
            CompanyId = CompanyId,
            OwnerType = ownerType,
            ExternalOwnerId = externalOwnerId,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = SeededAt
        };

    private static StockItem CreateStockItem(
        string stockItemId,
        InventoryOwner owner,
        string productId,
        int quantity,
        string seedReference)
    {
        var stockItem = new StockItem
        {
            StockItemId = Guid.Parse(stockItemId),
            CompanyId = CompanyId,
            InventoryOwnerId = owner.InventoryOwnerId,
            InventoryOwner = owner,
            ProductId = Guid.Parse(productId),
            BatchId = Guid.Parse(productId.Replace("40000000", "50000000"))
        };

        var movement = new StockMovement
        {
            StockMovementId = Guid.Parse(stockItemId.Replace("70000000", "80000000")),
            StockItemId = stockItem.StockItemId,
            StockItem = stockItem,
            MovementType = StockMovementType.Adjustment,
            OnHandDelta = quantity,
            ReservedDelta = 0,
            ActorId = "development-seed",
            ReferenceType = "StagingSeed",
            ReferenceId = seedReference,
            Reason = "Initial staging inventory",
            OccurredAt = SeededAt
        };

        stockItem.ApplyMovement(movement);
        return stockItem;
    }
}
