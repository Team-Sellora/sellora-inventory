namespace Sellora.InventoryService.Application.Stock;

public sealed record ResolveFulfilmentRequest(
    string OrderReference,
    Guid AgencyId,
    IReadOnlyCollection<ReservationLineRequest> Lines);