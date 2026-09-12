namespace Sellora.InventoryService.Application.Stock;

public enum ReservationOutcome
{
    Success = 1,
    InvalidRequest = 2,
    TenantNotAvailable = 3,
    InventoryOwnerNotFound = 4,
    InsufficientStock = 5,
    ReservationNotFound = 6,
    ReservationAlreadyReleased = 7,
    ReservationAlreadyConfirmed = 8
}