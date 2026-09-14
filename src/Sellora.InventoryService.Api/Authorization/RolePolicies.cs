using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Sellora.InventoryService.Api.Authorization;

public static class RolePolicies
{
    public const string RequireStockAdjustment = "RequireStockAdjustment";
    public const string RequireStockRead = "RequireStockRead";
    public const string RequireStockReservation = "RequireStockReservation";

    public static void AddSelloraInventoryPolicies(
    this AuthorizationOptions options)
    {
        options.AddPolicy(RequireStockAdjustment, policy =>
            policy.RequireAssertion(context =>
                HasRole(context, "CompanyAdmin", "AgencyOperator")));

        options.AddPolicy(RequireStockRead, policy =>
            policy.RequireAssertion(context =>
                HasRole(
                    context,
                    "CompanyAdmin",
                    "AgencyOperator",
                    "SalesRep")));

        options.AddPolicy(RequireStockReservation, policy =>
            policy.RequireAssertion(context =>
                HasRole(
                    context,
                    "CompanyAdmin",
                    "AgencyOperator",
                    "SalesRep")));
    }

    private static bool HasRole(
    AuthorizationHandlerContext context,
    params string[] roles) =>
    roles.Any(role =>
        context.User.HasClaim(ClaimTypes.Role, role) ||
        context.User.HasClaim("roles", role));
}