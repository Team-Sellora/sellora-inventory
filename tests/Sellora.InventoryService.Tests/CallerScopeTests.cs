using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Api.Identity;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Tests;

/// <summary>Agency / sales-rep IDs come from Organization, not token claims.</summary>
public sealed class CallerScopeTests
{
    private sealed class FakeOrganization : IOrganizationScopeClient
    {
        public CallerScope? Scope { get; set; }
        public bool Down { get; set; }
        public int Calls { get; private set; }

        public Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Down) throw new OrganizationUnavailableException("down");
            return Task.FromResult(Scope);
        }
    }

    private readonly FakeOrganization _organization = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private async Task<(HttpContext Context, bool NextCalled)> RunAsync(string role, params Claim[] extra)
    {
        var claims = new List<Claim> { new("sub", "user-1"), new("roles", role), new("companyId", "c-1") };
        claims.AddRange(extra);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "sub", "roles"))
        };
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        await new CallerScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; })
            .InvokeAsync(context, _organization, _cache,
                Options.Create(new CallerScopeOptions()), NullLogger<CallerScopeMiddleware>.Instance);

        return (context, nextCalled);
    }

    [Fact]
    public async Task Agency_comes_from_organization_and_a_stale_claim_is_ignored()
    {
        var agency = Guid.NewGuid();
        _organization.Scope = new CallerScope(agency, null);

        var (context, nextCalled) = await RunAsync("AgencyOperator", new Claim("agencyId", Guid.NewGuid().ToString()));
        var caller = new HttpCurrentUserContext(new HttpContextAccessor { HttpContext = context });

        Assert.True(nextCalled);
        Assert.Equal(agency, caller.AgencyId);
        Assert.Null(caller.SalesRepId);
    }

    [Fact]
    public async Task Scope_is_cached_per_user()
    {
        _organization.Scope = new CallerScope(null, Guid.NewGuid());

        await RunAsync("SalesRep");
        await RunAsync("SalesRep");

        Assert.Equal(1, _organization.Calls);
    }

    [Fact]
    public async Task Company_admin_skips_the_lookup()
    {
        _organization.Down = true;

        var (_, nextCalled) = await RunAsync("CompanyAdmin");

        Assert.True(nextCalled);
        Assert.Equal(0, _organization.Calls);
    }

    [Fact]
    public async Task Organization_down_is_503()
    {
        _organization.Down = true;

        var (context, nextCalled) = await RunAsync("SalesRep");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task No_profile_means_no_ids()
    {
        _organization.Scope = null;

        var (context, _) = await RunAsync("SalesRep", new Claim("salesRepId", Guid.NewGuid().ToString()));
        var caller = new HttpCurrentUserContext(new HttpContextAccessor { HttpContext = context });

        Assert.Null(caller.SalesRepId);
    }
}
