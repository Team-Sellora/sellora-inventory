namespace Sellora.InventoryService.Application.Events;

public sealed record OrderCancelledEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    Guid ReservationId,
    string OrderReference,
    DateTimeOffset CancelledAt,
    string CorrelationId);
