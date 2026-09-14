namespace Sellora.InventoryService.Application.Events;

public interface IOrderEventHandler
{
    Task HandleAsync(
        OrderConfirmedEvent @event,
        CancellationToken cancellationToken = default);
}
