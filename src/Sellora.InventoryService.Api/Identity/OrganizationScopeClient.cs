using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Api.Identity;

public sealed class OrganizationOptions
{
    public const string Section = "Dependencies:Organization";

    public string BaseUrl { get; set; } = string.Empty;

    public double TimeoutSeconds { get; set; } = 3;
}

/// <summary>
/// Calls Organization's GET /api/me/scope with the caller's own bearer
/// token, so Organization resolves the user from the verified sub.
/// </summary>
public sealed class OrganizationScopeClient(
    HttpClient http,
    IHttpContextAccessor accessor) : IOrganizationScopeClient
{
    public async Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken)
    {
        if (http.BaseAddress is null)
        {
            throw new OrganizationUnavailableException(
                "Dependencies:Organization:BaseUrl is not configured, so the caller's scope cannot be resolved.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/me/scope");

        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization) &&
            AuthenticationHeaderValue.TryParse(authorization, out var header))
        {
            request.Headers.Authorization = header;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new OrganizationUnavailableException("Organization service could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new OrganizationUnavailableException(
                    $"Organization service returned HTTP {(int)response.StatusCode} for the caller's scope.");
            }

            var scope = await response.Content.ReadFromJsonAsync<ScopeDto>(cancellationToken)
                ?? throw new OrganizationUnavailableException("Organization service returned an empty scope.");

            return new CallerScope(scope.AgencyId, scope.SalesRepId);
        }
    }

    private sealed record ScopeDto(Guid? AgencyId, Guid? SalesRepId);
}
