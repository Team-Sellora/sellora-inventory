namespace Sellora.InventoryService.Application.Stock;

public sealed record ReserveStockResult(
    ReservationOutcome Outcome,
    StockReservationResponse? Reservation,
    IReadOnlyCollection<ReservationShortage> Shortages,
    string? Message)
{
    public static ReserveStockResult Success(
        StockReservationResponse reservation) =>
        new(
            ReservationOutcome.Success,
            reservation,
            Array.Empty<ReservationShortage>(),
            null);

    public static ReserveStockResult Failure(
        ReservationOutcome outcome,
        string message,
        IReadOnlyCollection<ReservationShortage>? shortages = null) =>
        new(
            outcome,
            null,
            shortages ?? Array.Empty<ReservationShortage>(),
            message);
}