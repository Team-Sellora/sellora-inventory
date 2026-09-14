namespace Sellora.InventoryService.Domain.Tenancy;

/// <summary>
/// Establishes an explicit tenant scope for trusted background processing.
/// </summary>
public interface ISystemTenantContext
{
    IDisposable BeginSystemTenantScope(Guid companyId);
}
