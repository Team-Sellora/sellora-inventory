namespace Sellora.InventoryService.Application.Identity;

/// <summary>
/// The caller's place in the hierarchy, from Organization's
/// GET /api/me/scope — the single source of truth. Replaces reading
/// agencyId / salesRepId claims from the access token.
/// </summary>
public sealed record CallerScope(Guid? AgencyId, Guid? SalesRepId)
{
    /// <summary>No profile in Organization: the caller sees nothing.</summary>
    public static CallerScope Empty { get; } = new(null, null);
}

public interface IOrganizationScopeClient
{
    /// <summary>Null when the caller has no active profile (404).</summary>
    /// <exception cref="OrganizationUnavailableException">Organization could not answer.</exception>
    Task<CallerScope?> GetCallerScopeAsync(CancellationToken cancellationToken);
}

public sealed class OrganizationUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
