using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Infrastructure.Kafka;

namespace Sellora.InventoryService.Infrastructure.OrderEvents;

public sealed class OrderEventConsumerService(
    IOptions<OrderEventConsumerOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<OrderEventConsumerService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BootstrapServers) ||
            string.IsNullOrWhiteSpace(settings.OrderConsumerGroupId) ||
            string.IsNullOrWhiteSpace(settings.OrderTopic) ||
            string.IsNullOrWhiteSpace(settings.DeliveryTopic) ||
            string.IsNullOrWhiteSpace(settings.DeadLetterTopic) ||
            settings.OrderTopic == settings.DeliveryTopic ||
            settings.DeadLetterTopic == settings.OrderTopic ||
            settings.DeadLetterTopic == settings.DeliveryTopic)
            throw new InvalidOperationException("Inventory event topics and consumer group must be configured and distinct.");

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 10000
        };

        KafkaSaslConfigurator.Apply(
            producerConfig, settings.SaslUsername, settings.SaslPassword);

        using var producer = new ProducerBuilder<string, string>(producerConfig).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = settings.OrderConsumerGroupId,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false
            };

            KafkaSaslConfigurator.Apply(
                consumerConfig, settings.SaslUsername, settings.SaslPassword);

            using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();


            try
            {
                consumer.Subscribe(new[] { settings.OrderTopic, settings.DeliveryTopic });
                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);
                    try
                    {
                        await ProcessAsync(record, settings, stoppingToken);
                    }
                    catch (Exception exception) when (
                        exception is JsonException or InvalidInventoryEventException)
                    {
                        // Publication must be acknowledged before advancing the offset.
                        // If Kafka fails, the outer loop replays the source record.
                        await producer.ProduceAsync(settings.DeadLetterTopic,
                            new Message<string, string>
                            {
                                Key = $"{record.Topic}:{record.Partition.Value}:{record.Offset.Value}",
                                Value = JsonSerializer.Serialize(new
                                {
                                    sourceTopic = record.Topic,
                                    sourcePartition = record.Partition.Value,
                                    sourceOffset = record.Offset.Value,
                                    messageKey = record.Message.Key,
                                    payload = record.Message.Value,
                                    headers = record.Message.Headers?.Select(header => new
                                    {
                                        key = header.Key,
                                        value = header.GetValueBytes() is byte[] bytes
                                            ? Convert.ToBase64String(bytes) : null
                                    }),
                                    reason = exception.Message,
                                    failedAt = DateTimeOffset.UtcNow
                                })
                            }, stoppingToken);

                        logger.LogError(exception,
                            "InventoryEventDeadLettered: {Topic}/{Partition}/{Offset} sent to {DeadLetterTopic}.",
                            record.Topic, record.Partition.Value, record.Offset.Value,
                            settings.DeadLetterTopic);
                    }

                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Inventory event processing interrupted. Rejoining from committed offsets; the failed event remains uncommitted.");
            }
            finally
            {
                try { consumer.Close(); }
                catch (KafkaException exception)
                {
                    logger.LogWarning(exception, "Kafka consumer could not close cleanly.");
                }
            }

            // Rejoin instead of holding a failed record past max.poll.interval.ms.
            // A fresh scope is created for every replay.
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> record,
        OrderEventConsumerOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.Message.Value))
            throw new InvalidInventoryEventException("Event payload is empty.");

        using var document = JsonDocument.Parse(record.Message.Value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidInventoryEventException("Event payload must be a JSON object.");

        var envelope = JsonSerializer.Deserialize<EventEnvelope>(record.Message.Value, JsonOptions);
        if (string.IsNullOrWhiteSpace(envelope?.EventType))
            throw new InvalidInventoryEventException("EventType is required.");

        var eventType = envelope.EventType;
        if (eventType is not ("OrderConfirmed" or "OrderCancelled" or "ReturnAccepted" or "VanStockReturned"))
        {
            logger.LogDebug("Ignoring unrelated event {EventType} on {Topic}.", eventType, record.Topic);
            return;
        }

        var expectedTopic = eventType == "ReturnAccepted" ? settings.DeliveryTopic : settings.OrderTopic;
        if (record.Topic != expectedTopic)
            throw new InvalidInventoryEventException("Inventory event arrived on the wrong source topic.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IOrderEventHandler>();
        var payload = record.Message.Value;
        switch (eventType)
        {
            case "OrderConfirmed":
                await handler.HandleAsync(Deserialize<OrderConfirmedEvent>(payload), cancellationToken);
                break;
            case "OrderCancelled":
                await handler.HandleAsync(Deserialize<OrderCancelledEvent>(payload), cancellationToken);
                break;
            case "ReturnAccepted":
                await handler.HandleAsync(Deserialize<ReturnAcceptedEvent>(payload), cancellationToken);
                break;
            case "VanStockReturned":
                // US-E4-6: published by sellora-order, so it arrives on the order topic.
                await handler.HandleAsync(Deserialize<VanStockReturnedEvent>(payload), cancellationToken);
                break;
        }
    }

    private static T Deserialize<T>(string payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new InvalidInventoryEventException("Event payload could not be deserialized.");

    private sealed record EventEnvelope(string EventType);
}
