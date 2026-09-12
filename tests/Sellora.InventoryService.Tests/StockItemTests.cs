using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;

namespace Sellora.InventoryService.Tests;

public sealed class StockItemTests
{
    [Fact]
    public void ApplyMovement_updates_balances_and_available_quantity()
    {
        var stockItem = NewStockItem();

        stockItem.ApplyMovement(Movement(
            stockItem,
            StockMovementType.Adjustment,
            onHandDelta: 10,
            reservedDelta: 0));

        stockItem.ApplyMovement(Movement(
            stockItem,
            StockMovementType.Reserved,
            onHandDelta: 0,
            reservedDelta: 4));

        Assert.Equal(10, stockItem.QuantityOnHand);
        Assert.Equal(4, stockItem.QuantityReserved);
        Assert.Equal(6, stockItem.AvailableQuantity);
        Assert.Equal(2, stockItem.RowVersion);
        Assert.Equal(2, stockItem.Movements.Count);
    }

    [Fact]
    public void ApplyMovement_rejects_negative_on_hand_stock()
    {
        var stockItem = NewStockItem();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            stockItem.ApplyMovement(Movement(
                stockItem,
                StockMovementType.Adjustment,
                onHandDelta: -1,
                reservedDelta: 0)));

        Assert.Equal("Stock on-hand quantity cannot be negative.", exception.Message);
    }

    [Fact]
    public void ApplyMovement_rejects_reserved_quantity_above_on_hand()
    {
        var stockItem = NewStockItem();

        stockItem.ApplyMovement(Movement(
            stockItem,
            StockMovementType.Adjustment,
            onHandDelta: 5,
            reservedDelta: 0));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            stockItem.ApplyMovement(Movement(
                stockItem,
                StockMovementType.Reserved,
                onHandDelta: 0,
                reservedDelta: 6)));

        Assert.Equal(
            "Reserved stock quantity cannot exceed on-hand quantity.",
            exception.Message);
    }

    [Fact]
    public void ApplyMovement_rejects_duplicate_ledger_movement()
    {
        var stockItem = NewStockItem();
        var movement = Movement(
            stockItem,
            StockMovementType.Adjustment,
            onHandDelta: 5,
            reservedDelta: 0);

        stockItem.ApplyMovement(movement);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            stockItem.ApplyMovement(movement));

        Assert.Equal(
            "The stock movement has already been applied.",
            exception.Message);
    }

    private static StockItem NewStockItem() =>
        new()
        {
            StockItemId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            InventoryOwnerId = Guid.NewGuid(),
            ProductId = Guid.NewGuid()
        };

    private static StockMovement Movement(
        StockItem stockItem,
        StockMovementType movementType,
        int onHandDelta,
        int reservedDelta) =>
        new()
        {
            StockMovementId = Guid.NewGuid(),
            StockItemId = stockItem.StockItemId,
            MovementType = movementType,
            OnHandDelta = onHandDelta,
            ReservedDelta = reservedDelta,
            ActorId = "test-user",
            Reason = "Test movement",
            OccurredAt = DateTimeOffset.UtcNow
        };
}