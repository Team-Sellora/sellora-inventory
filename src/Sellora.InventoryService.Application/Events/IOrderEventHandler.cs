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

    /// <summary>US-E4-6: move a rep's accepted van return to the agency's stock.</summary>
    Task HandleAsync(
        VanStockReturnedEvent @event,
        CancellationToken cancellationToken = default);
}
