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

    // Hierarchy IDs come from Organization (CallerScopeMiddleware), never
    // from token claims, so nobody copies database IDs into WSO2 IS.
    private CallerScope Scope =>
        httpContextAccessor.HttpContext?.Items[CallerScopeMiddleware.ItemKey] as CallerScope
        ?? CallerScope.Empty;

    public Guid? AgencyId => Scope.AgencyId;

    public Guid? SalesRepId => Scope.SalesRepId;
}
