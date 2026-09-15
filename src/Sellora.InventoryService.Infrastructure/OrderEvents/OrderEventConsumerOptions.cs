namespace Sellora.InventoryService.Infrastructure.OrderEvents;

public sealed class OrderEventConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string OrderTopic { get; init; } = "sellora.order.v1";

    public string DeliveryTopic { get; init; } = "sellora.delivery.v1";

    public string DeadLetterTopic { get; init; } = "sellora.inventory.dead-letter.v1";

    public string OrderConsumerGroupId { get; init; } =
        "sellora.inventory.order.v1";
}
