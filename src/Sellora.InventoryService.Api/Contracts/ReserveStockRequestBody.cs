namespace Sellora.InventoryService.Api.Contracts;

public sealed class ReserveStockRequestBody
{
    public string? OrderReference { get; init; }

    public Guid InventoryOwnerId { get; init; }

    public IReadOnlyCollection<ReservationLineRequestBody> Lines { get; init; } =
        Array.Empty<ReservationLineRequestBody>();
}