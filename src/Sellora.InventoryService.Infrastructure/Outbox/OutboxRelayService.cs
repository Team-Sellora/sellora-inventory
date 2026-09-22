using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Infrastructure.Kafka;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.OrderEvents;

namespace Sellora.InventoryService.Infrastructure.Outbox;

public sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderEventConsumerOptions> options,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RelayAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Inventory outbox relay failed."); }
        }
    }

    private async Task RelayAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages.IgnoreQueryFilters()
            .Where(x => x.PublishedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt).Take(50).ToListAsync(cancellationToken);
        if (messages.Count == 0) return;

        var settings = options.Value;
        var config = new ProducerConfig { BootstrapServers = settings.BootstrapServers };
        KafkaSaslConfigurator.Apply(config, settings.SaslUsername, settings.SaslPassword);
        using var producer = new ProducerBuilder<Null, string>(config).Build();
        var topic = settings.InventoryTopic;
        foreach (var message in messages)
        {
            try
            {
                await producer.ProduceAsync(topic, new Message<Null, string> { Value = message.Payload }, cancellationToken);
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.AttemptCount++;
                message.LastError = ex.Message[..Math.Min(2000, ex.Message.Length)];
                message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * message.AttemptCount));
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
