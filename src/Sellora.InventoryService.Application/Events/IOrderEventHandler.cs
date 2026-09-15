namespace Sellora.InventoryService.Application.Events;

public interface IOrderEventHandler
{
    Task HandleAsync(OrderCancelledEvent @event,
        CancellationToken cancellationToken = default);

    Task HandleAsync(ReturnAcceptedEvent @event,
        CancellationToken cancellationToken = default);

    Task HandleAsync(
        OrderConfirmedEvent @event,
        CancellationToken cancellationToken = default);
}
