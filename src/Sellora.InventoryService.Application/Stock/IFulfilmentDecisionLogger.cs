namespace Sellora.InventoryService.Application.Stock;

public interface IFulfilmentDecisionLogger
{
    void LogDecision(
        string orderReference,
        Guid agencyId,
        Guid? inventoryOwnerId,
        string decision,
        string reason);
}