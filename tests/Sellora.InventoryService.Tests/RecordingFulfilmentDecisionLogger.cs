using Sellora.InventoryService.Application.Stock;

namespace Sellora.InventoryService.Tests;

internal sealed class RecordingFulfilmentDecisionLogger
    : IFulfilmentDecisionLogger
{
    public List<Entry> Entries { get; } = new();

    public void LogDecision(
        string orderReference,
        Guid agencyId,
        Guid? inventoryOwnerId,
        string decision,
        string reason)
    {
        Entries.Add(new Entry(
            orderReference,
            agencyId,
            inventoryOwnerId,
            decision,
            reason));
    }

    internal sealed record Entry(
        string OrderReference,
        Guid AgencyId,
        Guid? InventoryOwnerId,
        string Decision,
        string Reason);
}