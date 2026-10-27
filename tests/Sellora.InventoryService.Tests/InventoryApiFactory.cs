using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Tests;

/// <summary>
/// Hosts the real Program in the "Testing" environment against the
/// PostgreSQL test container. Only the token validation and the
/// Organization dependency are replaced.
/// </summary>
public sealed class InventoryApiFactory(string connectionString)
    : WebApplicationFactory<Program>
{
    public FakeOrganizationScopeClient Organization { get; } = new();

    public HttpClient CreateClientFor(
        string? role,
        Guid? companyId,
        string? subject = "test-user")
    {
        var client = CreateClient();

        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        }

        if (companyId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.CompanyHeader, companyId.Value.ToString());
        }

        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject ?? string.Empty);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);

        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IOrganizationScopeClient>();
            services.AddSingleton<IOrganizationScopeClient>(provider =>
            {
                Organization.Accessor = provider.GetRequiredService<IHttpContextAccessor>();
                return Organization;
            });
        });
    }
}

public sealed class FakeOrganizationScopeClient : IOrganizationScopeClient
{
    private readonly Dictionary<string, CallerScope?> _scopes = new();

    public void SetScope(string subject, CallerScope? scope) => _scopes[subject] = scope;

    internal IHttpContextAccessor? Accessor { get; set; }

    public Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
    {
        // The middleware asks on behalf of the current caller; tests key the
        // answer by the subject they send.
        var subject = Accessor?.HttpContext?.User.FindFirst("sub")?.Value ?? string.Empty;
        return Task.FromResult(_scopes.TryGetValue(subject, out var scope) ? scope : null);
    }
}

/// <summary>Authenticates from test headers instead of a WSO2 token.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RoleHeader = "X-Test-Role";
    public const string CompanyHeader = "X-Test-Company";
    public const string SubjectHeader = "X-Test-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("roles", role.ToString()) };

        var subject = Request.Headers[SubjectHeader].ToString();
        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new Claim("sub", subject));
        }

        if (Request.Headers.TryGetValue(CompanyHeader, out var company))
        {
            claims.Add(new Claim("companyId", company.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "sub", "roles");

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
