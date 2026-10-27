using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Infrastructure.HierarchyEvents;
using Sellora.InventoryService.Infrastructure.OrderEvents;

namespace Sellora.InventoryService.Tests;

/// <summary>Routing and validation of Kafka payloads, without a broker.</summary>
public sealed class EventConsumerTests
{
    private sealed class RecordingOrderHandler : IOrderEventHandler
    {
        public List<object> Handled { get; } = new();

        public Task HandleAsync(OrderCancelledEvent @event, CancellationToken cancellationToken = default) => Record(@event);
        public Task HandleAsync(ReturnAcceptedEvent @event, CancellationToken cancellationToken = default) => Record(@event);
        public Task HandleAsync(OrderConfirmedEvent @event, CancellationToken cancellationToken = default) => Record(@event);
        public Task HandleAsync(VanStockReturnedEvent @event, CancellationToken cancellationToken = default) => Record(@event);

        private Task Record(object @event)
        {
            Handled.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHierarchyHandler : IHierarchyEventHandler
    {
        public List<object> Handled { get; } = new();
        public Exception? Failure { get; set; }

        public Task HandleAsync(AgencyRegisteredEvent @event, CancellationToken cancellationToken = default) => Record(@event);
        public Task HandleAsync(SalesRepAssignedEvent @event, CancellationToken cancellationToken = default) => Record(@event);

        private Task Record(object @event)
        {
            if (Failure is not null) throw Failure;
            Handled.Add(@event);
            return Task.CompletedTask;
        }
    }

    private readonly OrderEventConsumerOptions _settings = new();
    private readonly RecordingOrderHandler _orderHandler = new();
    private readonly RecordingHierarchyHandler _hierarchyHandler = new();

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddSingleton<IOrderEventHandler>(_orderHandler)
        .AddSingleton<IHierarchyEventHandler>(_hierarchyHandler)
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    private OrderEventConsumerService OrderConsumer(OrderEventConsumerOptions? settings = null) => new(
        Options.Create(settings ?? _settings), Scopes(), NullLogger<OrderEventConsumerService>.Instance);

    private HierarchyEventConsumerService HierarchyConsumer() => new(
        Options.Create(new HierarchyConsumerOptions()), Scopes(), NullLogger<HierarchyEventConsumerService>.Instance);

    private static ConsumeResult<string, string> Record(string topic, string? payload, Headers? headers = null) => new()
    {
        Topic = topic,
        Partition = new Partition(2),
        Offset = new Offset(42),
        Message = new Message<string, string> { Key = "order-1", Value = payload!, Headers = headers }
    };

    private static string Payload(object @event) => JsonSerializer.Serialize(@event);

    private static readonly Guid Company = Guid.NewGuid();

    public static TheoryData<string, Type> RoutedEvents() => new()
    {
        { Payload(new OrderConfirmedEvent(Guid.NewGuid(), "OrderConfirmed", "1.0", Company, Guid.NewGuid(), Guid.NewGuid(), "ORD-1", DateTimeOffset.UtcNow, "c")), typeof(OrderConfirmedEvent) },
        { Payload(new OrderCancelledEvent(Guid.NewGuid(), "OrderCancelled", "1.0", Company, Guid.NewGuid(), Guid.NewGuid(), "ORD-1", DateTimeOffset.UtcNow, "c")), typeof(OrderCancelledEvent) },
        { Payload(new VanStockReturnedEvent(Guid.NewGuid(), "VanStockReturned", "1.0", Company, Guid.NewGuid(), "VR-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Array.Empty<VanStockReturnedLine>(), DateTimeOffset.UtcNow, "c")), typeof(VanStockReturnedEvent) },
    };

    [Theory]
    [MemberData(nameof(RoutedEvents))]
    public async Task Order_topic_events_are_routed_to_the_handler(string payload, Type expected)
    {
        await OrderConsumer().ProcessAsync(Record(_settings.OrderTopic, payload), _settings, CancellationToken.None);

        Assert.IsType(expected, Assert.Single(_orderHandler.Handled));
    }

    [Fact]
    public async Task Returns_are_routed_from_the_delivery_topic()
    {
        var payload = Payload(new ReturnAcceptedEvent(Guid.NewGuid(), "ReturnAccepted", "1.0", Company, Guid.NewGuid(),
            Guid.NewGuid(), "RET-1", new[] { new ReturnAcceptedLine(Guid.NewGuid(), null, 1) }, DateTimeOffset.UtcNow, "c"));

        await OrderConsumer().ProcessAsync(Record(_settings.DeliveryTopic, payload), _settings, CancellationToken.None);

        Assert.IsType<ReturnAcceptedEvent>(Assert.Single(_orderHandler.Handled));
    }

    [Fact]
    public async Task Unrelated_events_are_ignored()
    {
        await OrderConsumer().ProcessAsync(
            Record(_settings.OrderTopic, """{"eventType":"OrderPlaced"}"""), _settings, CancellationToken.None);

        Assert.Empty(_orderHandler.Handled);
    }

    [Theory]
    [InlineData("sellora.order.v1", "")]
    [InlineData("sellora.order.v1", "[1,2]")]
    [InlineData("sellora.order.v1", """{"eventType":""}""")]
    [InlineData("sellora.delivery.v1", """{"eventType":"OrderConfirmed"}""")]
    public async Task Invalid_order_events_are_permanent_failures(string topic, string payload)
    {
        await Assert.ThrowsAsync<InvalidInventoryEventException>(() =>
            OrderConsumer().ProcessAsync(Record(topic, payload), _settings, CancellationToken.None));

        Assert.Empty(_orderHandler.Handled);
    }

    [Fact]
    public async Task Malformed_json_is_a_permanent_failure()
    {
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            OrderConsumer().ProcessAsync(Record(_settings.OrderTopic, "{not json"), _settings, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "group", "a", "b", "c")]
    [InlineData("broker", "", "a", "b", "c")]
    [InlineData("broker", "group", "a", "a", "c")]
    [InlineData("broker", "group", "a", "b", "a")]
    [InlineData("broker", "group", "a", "b", "b")]
    public void Topics_must_be_configured_and_distinct(
        string servers, string group, string order, string delivery, string deadLetter)
    {
        var settings = new OrderEventConsumerOptions
        {
            BootstrapServers = servers, OrderConsumerGroupId = group,
            OrderTopic = order, DeliveryTopic = delivery, DeadLetterTopic = deadLetter
        };

        Assert.Throws<InvalidOperationException>(() => OrderEventConsumerService.ValidateSettings(settings));
    }

    [Fact]
    public async Task The_consumer_refuses_to_start_with_invalid_topics()
    {
        var consumer = OrderConsumer(new OrderEventConsumerOptions { DeadLetterTopic = "sellora.order.v1" });

        await consumer.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.ExecuteTask!);
    }

    [Fact]
    public void Dead_letters_keep_the_source_position_payload_and_headers()
    {
        var headers = new Headers { { "traceparent", Encoding.UTF8.GetBytes("trace-1") } };
        var record = Record(_settings.OrderTopic, "{bad", headers);

        var message = OrderEventConsumerService.BuildDeadLetterMessage(
            record, new InvalidInventoryEventException("broken"));

        Assert.Equal($"{_settings.OrderTopic}:2:42", message.Key);
        using var body = JsonDocument.Parse(message.Value);
        Assert.Equal(42, body.RootElement.GetProperty("sourceOffset").GetInt64());
        Assert.Equal("{bad", body.RootElement.GetProperty("payload").GetString());
        Assert.Equal("broken", body.RootElement.GetProperty("reason").GetString());
        var header = Assert.Single(body.RootElement.GetProperty("headers").EnumerateArray());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("trace-1")), header.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Hierarchy_events_are_routed_by_event_type()
    {
        var consumer = HierarchyConsumer();
        var agency = Payload(new AgencyRegisteredEvent(Guid.NewGuid(), "AgencyRegistered", "1.0", Company, Guid.NewGuid(),
            Guid.NewGuid(), "Colombo", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "c"))
            .Replace("\"EventType\"", "\"eventType\"");
        var rep = Payload(new SalesRepAssignedEvent(Guid.NewGuid(), "SalesRepAssigned", "1.0", Company, Guid.NewGuid(),
            Guid.NewGuid(), "Ruwan", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "c"))
            .Replace("\"EventType\"", "\"eventType\"");

        Assert.True(await consumer.ProcessAsync(agency, CancellationToken.None));
        Assert.True(await consumer.ProcessAsync(rep, CancellationToken.None));
        Assert.True(await consumer.ProcessAsync("""{"eventType":"TerritoryCreated"}""", CancellationToken.None));
        Assert.True(await consumer.ProcessAsync("""{"noEventType":true}""", CancellationToken.None));

        Assert.Collection(_hierarchyHandler.Handled,
            first => Assert.IsType<AgencyRegisteredEvent>(first),
            second => Assert.IsType<SalesRepAssignedEvent>(second));
    }

    [Fact]
    public async Task Malformed_hierarchy_events_are_committed_but_failures_are_retried()
    {
        var consumer = HierarchyConsumer();

        Assert.True(await consumer.ProcessAsync("{not json", CancellationToken.None));

        _hierarchyHandler.Failure = new InvalidOperationException("database down");
        Assert.False(await consumer.ProcessAsync("""{"eventType":"AgencyRegistered"}""", CancellationToken.None));
    }
}
