using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Stock;

namespace Sellora.InventoryService.Tests;

/// <summary>US-E5-4: LowStockDetected says who holds the stock, so Notification can tell that agency.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class LowStockEventTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();

    public LowStockEventTests(PostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_low_stock_event_carries_the_owner_type_id_and_name()
    {
        var agencyId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var item = new StockItem
        {
            StockItemId = Guid.NewGuid(), CompanyId = _companyId, InventoryOwnerId = ownerId,
            ProductId = Guid.NewGuid(), ReorderThreshold = 10
        };
        item.ApplyMovement(new StockMovement
        {
            StockMovementId = Guid.NewGuid(), StockItemId = item.StockItemId,
            MovementType = StockMovementType.Adjustment, OnHandDelta = 4, ReservedDelta = 0,
            ActorId = "test", Reason = "Seed", OccurredAt = DateTimeOffset.UtcNow
        });

        await using (var seed = _fixture.CreateContext(_companyId))
        {
            seed.InventoryOwners.Add(new InventoryOwner
            {
                InventoryOwnerId = ownerId, CompanyId = _companyId, OwnerType = InventoryOwnerType.Agency,
                ExternalOwnerId = agencyId, DisplayName = "Colombo Agency", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext(_companyId))
        {
            await new LowStockDetectionService(db).EvaluateAfterDecrementAsync(item.StockItemId, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = _fixture.CreateContext(_companyId);
        var message = await check.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.AggregateId == item.StockItemId && m.EventType == "LowStockDetected");
        using var payload = JsonDocument.Parse(message.Payload);

        Assert.Equal("Agency", payload.RootElement.GetProperty("OwnerType").GetString());
        Assert.Equal(agencyId, payload.RootElement.GetProperty("ExternalOwnerId").GetGuid());
        Assert.Equal("Colombo Agency", payload.RootElement.GetProperty("OwnerDisplayName").GetString());
        Assert.Equal(4, payload.RootElement.GetProperty("AvailableQuantity").GetInt32());
    }
}
