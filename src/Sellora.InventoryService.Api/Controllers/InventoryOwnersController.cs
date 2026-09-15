using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Api.Authorization;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;

namespace Sellora.InventoryService.Api.Controllers;

[ApiController]
[Route("api/inventory-owners")]
public sealed class InventoryOwnersController(
    InventoryDbContext db,
    ITenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = RolePolicies.RequireStockRead)]
    public async Task<ActionResult<IReadOnlyCollection<InventoryOwnerResponse>>> GetOwners(
        CancellationToken cancellationToken)
    {
        if (tenantContext.CompanyId is null)
        {
            return Unauthorized(new
            {
                Message = "A valid company identifier was not found in the access token."
            });
        }

        var owners = await db.InventoryOwners
            .AsNoTracking()
            .Where(owner => owner.IsActive)
            .OrderBy(owner => owner.OwnerType)
            .ThenBy(owner => owner.DisplayName)
            .Select(owner => new InventoryOwnerResponse(
                owner.InventoryOwnerId,
                owner.OwnerType.ToString(),
                owner.ExternalOwnerId,
                owner.DisplayName))
            .ToListAsync(cancellationToken);

        return Ok(owners);
    }
}
