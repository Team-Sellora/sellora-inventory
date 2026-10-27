namespace Sellora.InventoryService.Api.Contracts;

public sealed class AdjustStockRequestBody
{
    public Guid InventoryOwnerId { get; init; }

    public Guid ProductId { get; init; }

    public Guid? BatchId { get; init; }

    public int QuantityDelta { get; init; }

    public string? Reason { get; init; }
}