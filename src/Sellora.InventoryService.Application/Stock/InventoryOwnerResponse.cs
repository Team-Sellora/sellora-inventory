namespace Sellora.InventoryService.Application.Stock;

public sealed record InventoryOwnerResponse(
    Guid InventoryOwnerId,
    string OwnerType,
    Guid ExternalOwnerId,
    string DisplayName);
