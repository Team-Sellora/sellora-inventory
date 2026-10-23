namespace Sellora.InventoryService.Domain.Tenancy;

public interface ITenantContext
{
    Guid? CompanyId { get; }
}