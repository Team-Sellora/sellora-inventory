namespace Sellora.InventoryService.Application.Stock;

public interface IStockReservationService
{
    Task<IReadOnlyCollection<StockAvailability>> CheckAvailabilityAsync(
        CheckAvailabilityRequest request,
        CancellationToken cancellationToken = default);

    Task<ReserveStockResult> ReserveAsync(
        ReserveStockRequest request,
        CancellationToken cancellationToken = default);

    Task<ReserveStockResult> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);

    Task<ReserveStockResult> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);
}