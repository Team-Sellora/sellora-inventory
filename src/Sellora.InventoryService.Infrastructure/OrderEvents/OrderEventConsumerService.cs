using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;

namespace Sellora.InventoryService.Infrastructure.OrderEvents;

public sealed class OrderEventConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly OrderEventConsumerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderEventConsumerService> _logger;

    public OrderEventConsumerService(
        IOptions<OrderEventConsumerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<OrderEventConsumerService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.OrderConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .Build();

        consumer.Subscribe(_options.OrderTopic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;

                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    _logger.LogError(
                        exception,
                        "Failed to consume an order event.");

                    continue;
                }

                if (await ProcessAsync(result.Message.Value, stoppingToken))
                {
                    consumer.Commit(result);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> ProcessAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty(
                    "eventType",
                    out var eventTypeProperty))
            {
                _logger.LogWarning(
                    "Ignoring order event without eventType.");

                return true;
            }

            if (eventTypeProperty.GetString() != "OrderConfirmed")
            {
                return true;
            }

            var @event = JsonSerializer.Deserialize<OrderConfirmedEvent>(
                payload,
                JsonOptions);

            if (@event is null)
            {
                throw new JsonException(
                    "OrderConfirmed event payload is invalid.");
            }

            await using var scope = _scopeFactory.CreateAsyncScope();

            var handler = scope.ServiceProvider
                .GetRequiredService<IOrderEventHandler>();

            await handler.HandleAsync(@event, cancellationToken);

            return true;
        }
        catch (JsonException exception)
        {
            _logger.LogError(
                exception,
                "Ignoring malformed order event payload.");

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Order event processing failed; message will be retried.");

            return false;
        }
    }
}