using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Api.Tenancy;

public sealed class HttpTenantContext(IHttpContextAccessor accessor)
    : ITenantContext, ISystemTenantContext
{
    private Guid? _systemCompanyId;

    public Guid? CompanyId
    {
        get
        {
            if (_systemCompanyId is not null)
            {
                return _systemCompanyId;
            }

            var value = accessor.HttpContext?.User
                .FindFirst("companyId")?.Value;

            return Guid.TryParse(value, out var companyId)
                ? companyId
                : null;
        }
    }

    public IDisposable BeginSystemTenantScope(Guid companyId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(companyId, Guid.Empty);

        var previousCompanyId = _systemCompanyId;
        _systemCompanyId = companyId;

        return new TenantScope(this, previousCompanyId);
    }

    private sealed class TenantScope(
        HttpTenantContext context,
        Guid? previousCompanyId) : IDisposable
    {
        public void Dispose() => context._systemCompanyId = previousCompanyId;
    }
}
