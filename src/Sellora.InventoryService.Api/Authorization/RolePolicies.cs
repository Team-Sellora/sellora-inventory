using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Sellora.InventoryService.Api.Authorization;

public static class RolePolicies
{
    public const string RequireStockAdjustment = "RequireStockAdjustment";

    public static void AddSelloraInventoryPolicies(
        this AuthorizationOptions options)
    {
        options.AddPolicy(RequireStockAdjustment, policy =>
            policy.RequireAssertion(context =>
                HasRole(context, "CompanyAdmin", "AgencyOperator")));
    }

    private static bool HasRole(
    AuthorizationHandlerContext context,
    params string[] roles) =>
    roles.Any(role =>
        context.User.HasClaim(ClaimTypes.Role, role) ||
        context.User.HasClaim("roles", role));
}