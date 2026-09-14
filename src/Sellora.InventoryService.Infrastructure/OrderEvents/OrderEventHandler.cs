using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Infrastructure.OrderEvents;

public sealed class OrderEventHandler : IOrderEventHandler
{
    private readonly IStockReservationService _reservationService;
    private readonly ISystemTenantContext _systemTenantContext;

    public OrderEventHandler(
        IStockReservationService reservationService,
        ISystemTenantContext systemTenantContext)
    {
        _reservationService = reservationService;
        _systemTenantContext = systemTenantContext;
    }

    public async Task HandleAsync(
        OrderConfirmedEvent @event,
        CancellationToken cancellationToken = default)
    {
        Validate(@event);

        using var tenantScope = _systemTenantContext
            .BeginSystemTenantScope(@event.CompanyId);

        var result = await _reservationService.ConfirmAsync(
            @event.ReservationId,
            cancellationToken);

        if (result.Outcome != ReservationOutcome.Success)
        {
            throw new InvalidOperationException(
                $"Order confirmation could not be applied: {result.Outcome}.");
        }
    }

    private static void Validate(OrderConfirmedEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.EventId == Guid.Empty ||
            @event.CompanyId == Guid.Empty ||
            @event.ReservationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(@event.OrderReference))
        {
            throw new InvalidOperationException(
                "OrderConfirmed event is missing a required value.");
        }
    }
}