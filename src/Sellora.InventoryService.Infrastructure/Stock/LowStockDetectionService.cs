using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class LowStockDetectionService(InventoryDbContext db)
{
    public async Task EvaluateAfterDecrementAsync(Guid stockItemId, CancellationToken cancellationToken)
    {
        var item = await db.StockItems.SingleAsync(x => x.StockItemId == stockItemId, cancellationToken);
        if (item.ReorderThreshold is not int threshold || item.LowStockNotified || item.AvailableQuantity >= threshold)
            return;

        var now = DateTimeOffset.UtcNow;
        item.LowStockNotified = true;
        db.OutboxMessages.Add(new OutboxMessage
        {
            OutboxId = Guid.NewGuid(), CompanyId = item.CompanyId, AggregateId = item.StockItemId,
            EventType = "LowStockDetected", OccurredAt = now, NextAttemptAt = now,
            Payload = JsonSerializer.Serialize(new LowStockDetectedEvent(Guid.NewGuid(), "LowStockDetected", "1.0",
                item.CompanyId, item.StockItemId, item.InventoryOwnerId, item.ProductId, item.BatchId,
                item.AvailableQuantity, threshold, now))
        });
    }

    public void RearmIfRestocked(StockItem item)
    {
        if (item.ReorderThreshold is int threshold && item.AvailableQuantity > threshold)
            item.LowStockNotified = false;
    }
}
