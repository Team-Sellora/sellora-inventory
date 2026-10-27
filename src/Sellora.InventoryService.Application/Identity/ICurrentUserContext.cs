namespace Sellora.InventoryService.Application.Identity;

public interface ICurrentUserContext
{
    string? Subject { get; }

    string? Role { get; }

    Guid? AgencyId { get; }

    Guid? SalesRepId { get; }
}