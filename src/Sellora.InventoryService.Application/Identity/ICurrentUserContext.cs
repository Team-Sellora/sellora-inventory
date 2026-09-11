namespace Sellora.InventoryService.Application.Identity;

public interface ICurrentUserContext
{
    string? Subject { get; }

    Guid? AgencyId { get; }
}