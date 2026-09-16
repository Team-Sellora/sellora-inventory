using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Infrastructure.Kafka;
using static Sellora.InventoryService.Infrastructure.Kafka.KafkaSaslConfigurator;

namespace Sellora.InventoryService.Infrastructure.HierarchyEvents;

public sealed class HierarchyEventConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HierarchyConsumerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HierarchyEventConsumerService> _logger;

    public HierarchyEventConsumerService(
        IOptions<HierarchyConsumerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<HierarchyEventConsumerService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Let the host finish starting Kestrel before entering the blocking
        // Kafka consume loop. Otherwise a broker connection attempt can keep
        // the HTTP server from binding to its configured port.
        await Task.Yield();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        KafkaSaslConfigurator.Apply(
            consumerConfig, _options.SaslUsername, _options.SaslPassword);

        using var consumer = new ConsumerBuilder<string, string>(
            consumerConfig).Build();


        consumer.Subscribe(_options.HierarchyTopic);

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
                        "Failed to consume a hierarchy event.");

                    continue;
                }

                var processed = await ProcessAsync(
                    result.Message.Value,
                    stoppingToken);

                if (processed)
                {
                    consumer.Commit(result);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
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
                    "Ignoring hierarchy event without eventType.");

                return true;
            }

            var eventType = eventTypeProperty.GetString();

            await using var scope = _scopeFactory.CreateAsyncScope();

            var handler = scope.ServiceProvider
                .GetRequiredService<IHierarchyEventHandler>();

            switch (eventType)
            {
                case "AgencyRegistered":
                    {
                        var @event =
                            JsonSerializer.Deserialize<AgencyRegisteredEvent>(
                                payload,
                                JsonOptions);

                        if (@event is null)
                        {
                            throw new JsonException(
                                "AgencyRegistered event payload is invalid.");
                        }

                        await handler.HandleAsync(@event, cancellationToken);
                        break;
                    }

                case "SalesRepAssigned":
                    {
                        var @event =
                            JsonSerializer.Deserialize<SalesRepAssignedEvent>(
                                payload,
                                JsonOptions);

                        if (@event is null)
                        {
                            throw new JsonException(
                                "SalesRepAssigned event payload is invalid.");
                        }

                        await handler.HandleAsync(@event, cancellationToken);
                        break;
                    }

                default:
                    _logger.LogDebug(
                        "Ignoring unsupported hierarchy event type {EventType}.",
                        eventType);
                    break;
            }

            return true;
        }
        catch (JsonException exception)
        {
            _logger.LogError(
                exception,
                "Ignoring malformed hierarchy event payload.");

            // Commit malformed messages so they do not block the consumer.
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Hierarchy event processing failed; message will be retried.");

            // Do not commit: Kafka will replay this message.
            return false;
        }
    }
}
