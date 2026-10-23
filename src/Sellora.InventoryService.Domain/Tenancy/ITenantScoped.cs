namespace Sellora.InventoryService.Domain.Tenancy;

public interface ITenantScoped
{
    Guid CompanyId { get; }
}