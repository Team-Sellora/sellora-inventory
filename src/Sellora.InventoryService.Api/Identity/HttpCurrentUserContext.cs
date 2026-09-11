using System.Security.Claims;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Api.Identity;

public sealed class HttpCurrentUserContext(
    IHttpContextAccessor httpContextAccessor)
    : ICurrentUserContext
{
    private ClaimsPrincipal? User =>
        httpContextAccessor.HttpContext?.User;

    public string? Subject =>
        User?.FindFirst("sub")?.Value
        ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? Role =>
        User?.FindFirst(ClaimTypes.Role)?.Value
        ?? User?.FindFirst("roles")?.Value
        ?? User?.FindFirst("role")?.Value;

    public Guid? AgencyId
    {
        get
        {
            var value = User?.FindFirst("agencyId")?.Value;

            return Guid.TryParse(value, out var agencyId)
                ? agencyId
                : null;
        }
    }
}