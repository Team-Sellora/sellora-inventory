namespace Sellora.InventoryService.Api.Contracts;

public sealed class UpdateReorderThresholdRequestBody
{
    public int? ReorderThreshold { get; init; }
}
