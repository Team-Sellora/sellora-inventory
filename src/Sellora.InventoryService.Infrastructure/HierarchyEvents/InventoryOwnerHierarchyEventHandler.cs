using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Infrastructure.HierarchyEvents;

public sealed class InventoryOwnerHierarchyEventHandler
    : IHierarchyEventHandler
{
    private readonly InventoryDbContext _db;

    public InventoryOwnerHierarchyEventHandler(InventoryDbContext db)
    {
        _db = db;
    }

    public async Task HandleAsync(
        AgencyRegisteredEvent @event,
        CancellationToken cancellationToken = default)
    {
        await UpsertOwnerAsync(
            @event.CompanyId,
            InventoryOwnerType.Company,
            @event.CompanyId,
            "Company Stock",
            @event.EffectiveAt,
            cancellationToken);

        await UpsertOwnerAsync(
            @event.CompanyId,
            InventoryOwnerType.Agency,
            @event.AgencyId,
            @event.AgencyName,
            @event.EffectiveAt,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleAsync(
        SalesRepAssignedEvent @event,
        CancellationToken cancellationToken = default)
    {
        await UpsertOwnerAsync(
            @event.CompanyId,
            InventoryOwnerType.Company,
            @event.CompanyId,
            "Company Stock",
            @event.EffectiveAt,
            cancellationToken);

        // Kafka ordering is guaranteed per key, not across Agency and Sales Rep
        // aggregates. Create a safe placeholder if the Agency event has not
        // arrived yet; AgencyRegistered will later replace it with agencyName.
        await UpsertOwnerAsync(
            @event.CompanyId,
            InventoryOwnerType.Agency,
            @event.AgencyId,
            $"Agency {@event.AgencyId}",
            @event.EffectiveAt,
            cancellationToken,
            replaceDisplayName: false);

        await UpsertOwnerAsync(
            @event.CompanyId,
            InventoryOwnerType.SalesRep,
            @event.SalesRepId,
            @event.SalesRepName,
            @event.EffectiveAt,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertOwnerAsync(
        Guid companyId,
        InventoryOwnerType ownerType,
        Guid externalOwnerId,
        string displayName,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken,
        bool replaceDisplayName = true)
    {
        var owner = await _db.InventoryOwners
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.CompanyId == companyId &&
                    candidate.OwnerType == ownerType &&
                    candidate.ExternalOwnerId == externalOwnerId,
                cancellationToken);

        if (owner is null)
        {
            _db.InventoryOwners.Add(new InventoryOwner
            {
                InventoryOwnerId = Guid.NewGuid(),
                CompanyId = companyId,
                OwnerType = ownerType,
                ExternalOwnerId = externalOwnerId,
                DisplayName = displayName,
                IsActive = true,
                CreatedAt = createdAt
            });

            return;
        }

        owner.IsActive = true;

        if (replaceDisplayName &&
            !string.Equals(
                owner.DisplayName,
                displayName,
                StringComparison.Ordinal))
        {
            owner.DisplayName = displayName;
        }
    }
}