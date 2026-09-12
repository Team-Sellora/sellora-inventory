using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class ReservationExpirySweeper : BackgroundService
{
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReservationExpirySweeper> _logger;
    private readonly TimeSpan _interval;

    public ReservationExpirySweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<StockReservationOptions> options,
        ILogger<ReservationExpirySweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _interval = TimeSpan.FromSeconds(
            Math.Max(5, options.Value.SweepIntervalSeconds));
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReleaseExpiredReservationsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed while releasing expired stock reservations.");
            }
        }
    }

    private async Task ReleaseExpiredReservationsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();

        var reservationService = scope.ServiceProvider
            .GetRequiredService<IStockReservationService>();

        var expiredReservationIds = await db.StockReservations
            .AsNoTracking()
            .Where(reservation =>
                reservation.Status == ReservationStatus.Active &&
                reservation.ExpiresAt <= DateTimeOffset.UtcNow)
            .OrderBy(reservation => reservation.ExpiresAt)
            .Take(BatchSize)
            .Select(reservation => reservation.ReservationId)
            .ToListAsync(cancellationToken);

        foreach (var reservationId in expiredReservationIds)
        {
            var result = await reservationService.ReleaseAsync(
                reservationId,
                cancellationToken);

            if (result.Outcome != ReservationOutcome.Success)
            {
                _logger.LogWarning(
                    "Expired reservation {ReservationId} was not released. " +
                    "Outcome: {Outcome}.",
                    reservationId,
                    result.Outcome);
            }
        }

        if (expiredReservationIds.Count > 0)
        {
            _logger.LogInformation(
                "Processed {ReservationCount} expired stock reservations.",
                expiredReservationIds.Count);
        }
    }
}