namespace Sellora.InventoryService.Application.Events;

/// <summary>
/// US-E4-6: published by sellora-order on the order topic when an agency
/// accepts a rep's van return. Only <see cref="VanStockReturnedLine.AcceptedQuantity"/>
/// moves; the payload also carries declared quantity and variance, which
/// Inventory ignores.
/// </summary>
public sealed record VanStockReturnedEvent(
    Guid EventId,
    string EventType,
    string SchemaVersion,
    Guid CompanyId,
    Guid EntityId,
    string ReturnReference,
    Guid SalesRepId,
    Guid AgencyId,
    Guid VanInventoryOwnerId,
    IReadOnlyCollection<VanStockReturnedLine> Lines,
    DateTimeOffset AcceptedAt,
    string CorrelationId);

public sealed record VanStockReturnedLine(Guid ProductId, int AcceptedQuantity);
