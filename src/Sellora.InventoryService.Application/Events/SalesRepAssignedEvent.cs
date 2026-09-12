namespace Sellora.InventoryService.Application.Events;

public sealed record SalesRepAssignedEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    Guid SalesRepId,
    string SalesRepName,
    Guid TerritoryId,
    Guid AgencyId,
    DateTimeOffset EffectiveAt,
    string CorrelationId);