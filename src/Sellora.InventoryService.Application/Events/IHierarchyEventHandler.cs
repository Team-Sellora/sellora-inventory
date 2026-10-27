namespace Sellora.InventoryService.Application.Events;

public interface IHierarchyEventHandler
{
    Task HandleAsync(
        AgencyRegisteredEvent @event,
        CancellationToken cancellationToken = default);

    Task HandleAsync(
        SalesRepAssignedEvent @event,
        CancellationToken cancellationToken = default);
}