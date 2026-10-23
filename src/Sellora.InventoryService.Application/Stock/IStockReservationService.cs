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

    /// <summary>
    /// Order cancelled (US-E4-5): releases a held reservation, or returns the
    /// stock of a confirmed one to on-hand. A reservation that is already
    /// released or expired reports ReservationAlreadyReleased, so a replayed
    /// event changes nothing.
    /// </summary>
    Task<ReserveStockResult> CancelForOrderAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);
}