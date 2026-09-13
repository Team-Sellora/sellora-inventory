using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Api.Tenancy;

public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid? CompanyId
    {
        get
        {
            var value = accessor.HttpContext?.User
                .FindFirst("companyId")?.Value;

            return Guid.TryParse(value, out var companyId)
                ? companyId
                : null;
        }
    }
}