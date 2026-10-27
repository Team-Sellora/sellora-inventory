namespace Sellora.InventoryService.Application.Events;

public sealed record AgencyRegisteredEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    Guid AgencyId,
    string AgencyName,
    Guid ProvinceId,
    Guid OperatorId,
    DateTimeOffset EffectiveAt,
    string CorrelationId);