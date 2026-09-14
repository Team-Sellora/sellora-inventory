namespace Sellora.InventoryService.Api.Contracts;

public sealed class ReservationLineRequestBody
{
    public Guid ProductId { get; init; }

    public Guid? BatchId { get; init; }

    public int Quantity { get; init; }
}