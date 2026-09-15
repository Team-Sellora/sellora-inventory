namespace Sellora.InventoryService.Application.Events;

public sealed record OrderConfirmedEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    Guid ReservationId,
    string OrderReference,
    DateTimeOffset ConfirmedAt,
    string CorrelationId);