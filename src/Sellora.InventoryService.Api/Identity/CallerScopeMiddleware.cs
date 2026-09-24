using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Api.Identity;

public sealed class CallerScopeOptions
{
    public const string Section = "CallerScope";

    public int CacheSeconds { get; set; } = 300;

    public int MissingProfileCacheSeconds { get; set; } = 30;
}

/// <summary>
/// Resolves the caller's agency / sales-rep ID from Organization once per
/// user (cached), so these IDs never need to be copied into WSO2 IS claims.
/// Company Admins are unrestricted in their company and skip the lookup.
/// </summary>
public sealed class CallerScopeMiddleware(RequestDelegate next)
{
    public const string ItemKey = "sellora.caller-scope";

    private static readonly string[] ScopedRoles = { "AreaManager", "AgencyOperator", "SalesRep", "ShopOwner" };

    public async Task InvokeAsync(
        HttpContext context,
        IOrganizationScopeClient organization,
        IMemoryCache cache,
        IOptions<CallerScopeOptions> options,
        ILogger<CallerScopeMiddleware> logger)
    {
        var user = context.User;
        var role = ScopedRoles.FirstOrDefault(candidate =>
            user.HasClaim("roles", candidate) || user.HasClaim(ClaimTypes.Role, candidate));

        if (user.Identity?.IsAuthenticated != true ||
            role is null ||
            user.HasClaim("roles", "CompanyAdmin") ||
            user.HasClaim(ClaimTypes.Role, "CompanyAdmin"))
        {
            await next(context);
            return;
        }

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var key = $"caller-scope:{user.FindFirst("companyId")?.Value}:{subject}:{role}";

        if (!cache.TryGetValue(key, out CallerScope? scope))
        {
            try
            {
                var resolved = await organization.GetCallerScopeAsync(context.RequestAborted);
                scope = resolved ?? CallerScope.Empty;

                cache.Set(key, scope, TimeSpan.FromSeconds(resolved is null
                    ? options.Value.MissingProfileCacheSeconds
                    : options.Value.CacheSeconds));
            }
            catch (OrganizationUnavailableException exception)
            {
                logger.LogWarning(exception, "Could not resolve the caller's scope from Organization");

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "30";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Dependency unavailable",
                    Detail = "Organization service is currently unavailable, so your access could not be resolved. Try again shortly."
                });
                return;
            }
        }

        context.Items[ItemKey] = scope;
        await next(context);
    }
}
