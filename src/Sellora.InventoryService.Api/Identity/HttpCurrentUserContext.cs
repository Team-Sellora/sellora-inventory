using System.Security.Claims;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Api.Identity;

public sealed class HttpCurrentUserContext(
    IHttpContextAccessor httpContextAccessor)
    : ICurrentUserContext
{
    public string? Subject =>
        httpContextAccessor.HttpContext?.User
            .FindFirst("sub")?.Value
        ?? httpContextAccessor.HttpContext?.User
            .FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public Guid? AgencyId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User
                .FindFirst("agencyId")?.Value;

            return Guid.TryParse(value, out var agencyId)
                ? agencyId
                : null;
        }
    }
}