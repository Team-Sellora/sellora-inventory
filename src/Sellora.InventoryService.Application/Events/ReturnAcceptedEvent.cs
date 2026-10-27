namespace Sellora.InventoryService.Application.Events;

public sealed record ReturnAcceptedEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    Guid InventoryOwnerId,
    string ReturnReference,
    IReadOnlyCollection<ReturnAcceptedLine> Lines,
    DateTimeOffset AcceptedAt,
    string CorrelationId);

public sealed record ReturnAcceptedLine(Guid ProductId, Guid? BatchId, int Quantity);
