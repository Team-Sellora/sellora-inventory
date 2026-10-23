namespace Sellora.InventoryService.Infrastructure.HierarchyEvents;

public sealed class HierarchyConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string HierarchyTopic { get; init; } = "sellora.hierarchy.v1";

    public string ConsumerGroupId { get; init; } =
        "sellora.inventory.hierarchy.v1";

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }
}
