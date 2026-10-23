namespace Sellora.InventoryService.Domain.Entities;

public sealed class StockReservationLine
{
    public Guid StockReservationLineId { get; set; }

    public Guid ReservationId { get; set; }

    public Guid StockItemId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? BatchId { get; set; }

    public int Quantity { get; set; }

    public StockReservation Reservation { get; set; } = null!;
}