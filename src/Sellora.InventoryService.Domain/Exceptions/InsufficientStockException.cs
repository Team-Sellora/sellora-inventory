namespace Sellora.InventoryService.Domain.Exceptions;

/// <summary>
/// Thrown when a stock movement would reduce on-hand stock below zero.
/// </summary>
public sealed class InsufficientStockException : Exception
{
    public InsufficientStockException()
        : base("Stock on-hand quantity cannot be negative.")
    {
    }
}
