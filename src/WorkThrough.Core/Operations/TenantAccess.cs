using System.Security.Claims;

namespace WorkThrough;

/// <summary>Which tenants' executions a principal may read and operate on (ADR-019).</summary>
/// <remarks>Store-wide access is the default. Restricted access sees only executions carrying one of its tenant IDs: untenanted work
/// and other tenants' work look absent, and store-wide operations are denied.</remarks>
public sealed class TenantAccess
{
    /// <summary>Bounds the tenant filter a restricted principal adds to every query.</summary>
    public const int MaximumTenants = 100;

    private TenantAccess(IReadOnlySet<string>? tenants) => Tenants = tenants;

    public static TenantAccess AllTenants { get; } = new(null);

    /// <summary>Null for store-wide access.</summary>
    public IReadOnlySet<string>? Tenants { get; }

    public bool IsRestricted => Tenants is not null;

    /// <summary>Access to exactly these tenants. An empty set is valid and sees nothing.</summary>
    public static TenantAccess Only(IEnumerable<string> tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        var tenants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenant in tenantIds)
        {
            AdmissionKeys.Validate(tenant ?? throw new ArgumentException("Tenant IDs cannot be null.", nameof(tenantIds)), KeyScope.Tenant, nameof(tenantIds));
            tenants.Add(tenant);
        }

        if (tenants.Count > MaximumTenants)
        {
            throw new ArgumentException($"Tenant access is limited to {MaximumTenants} tenants per principal.", nameof(tenantIds));
        }

        return new(tenants);
    }

    public bool Allows(string? tenantId) => Tenants is null || tenantId is not null && Tenants.Contains(tenantId);
}

/// <summary>Maps a host-authenticated principal to its tenant access. Register one to enable tenant isolation in the dashboard and MCP.</summary>
/// <remarks>Derive tenants only from trusted claims or server-side lookups, never from request parameters. Exceptions deny the request.</remarks>
public interface ITenantAccessResolver
{
    ValueTask<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}
