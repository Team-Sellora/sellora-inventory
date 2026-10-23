namespace Sellora.InventoryService.Application.Stock;

public sealed class StockReservationOptions
{
    public const string SectionName = "StockReservation";

    public int TtlMinutes { get; init; } = 15;

    public int SweepIntervalSeconds { get; init; } = 60;
}