using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Tests;

internal sealed class FixedTenantContext(Guid? companyId) : ITenantContext
{
    public Guid? CompanyId { get; } = companyId;
}

internal sealed class NoOpSystemTenantContext : ISystemTenantContext
{
    public IDisposable BeginSystemTenantScope(Guid companyId) => new Scope();

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            // Nothing to restore: the test context already has a fixed tenant.
        }
    }
}

internal sealed class FakeCurrentUser(
    string? role,
    Guid? agencyId = null,
    Guid? salesRepId = null,
    string? subject = "test-user") : ICurrentUserContext
{
    public string? Subject { get; } = subject;
    public string? Role { get; } = role;
    public Guid? AgencyId { get; } = agencyId;
    public Guid? SalesRepId { get; } = salesRepId;
}

/// <summary>Builds tenant-scoped owners and stock rows with a matching ledger entry.</summary>
internal static class TestData
{
    public static InventoryOwner Owner(
        Guid companyId,
        InventoryOwnerType ownerType,
        Guid? externalOwnerId = null,
        string displayName = "Test owner",
        bool isActive = true) => new()
    {
        InventoryOwnerId = Guid.NewGuid(),
        CompanyId = companyId,
        OwnerType = ownerType,
        ExternalOwnerId = externalOwnerId ?? Guid.NewGuid(),
        DisplayName = displayName,
        IsActive = isActive,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static StockItem Stock(
        InventoryOwner owner,
        int quantity,
        Guid? productId = null,
        Guid? batchId = null,
        int? reorderThreshold = null)
    {
        var item = new StockItem
        {
            StockItemId = Guid.NewGuid(),
            CompanyId = owner.CompanyId,
            InventoryOwnerId = owner.InventoryOwnerId,
            ProductId = productId ?? Guid.NewGuid(),
            BatchId = batchId,
            ReorderThreshold = reorderThreshold
        };

        item.ApplyMovement(new StockMovement
        {
            StockMovementId = Guid.NewGuid(),
            StockItemId = item.StockItemId,
            MovementType = StockMovementType.Adjustment,
            OnHandDelta = quantity,
            ReservedDelta = 0,
            ActorId = "test",
            Reason = "Seed",
            OccurredAt = DateTimeOffset.UtcNow
        });

        return item;
    }
}
