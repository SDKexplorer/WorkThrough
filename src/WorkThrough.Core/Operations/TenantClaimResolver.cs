using System.Security.Claims;

namespace WorkThrough;

/// <summary>
/// Users carrying tenant claims, issued by the identity provider, see only those tenants' executions in the dashboard and MCP.
/// Users granted <see cref="AllTenants"/> by the host's role mapping see the whole store. Everyone else sees nothing, so a tenant
/// claim the provider forgot to issue hides data instead of revealing it. Never derive tenants from request input.
/// </summary>
public sealed class TenantClaimResolver(string claimType = TenantClaimResolver.DefaultClaimType) : ITenantAccessResolver
{
    public const string DefaultClaimType = "tenant";

    /// <summary>A host-mapped permission for operators who work across tenants.</summary>
    public const string AllTenants = "tenants.all";

    public ValueTask<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tenants = user.FindAll(claimType).Select(claim => claim.Value).ToArray();
        if (tenants.Length > 0)
        {
            return ValueTask.FromResult(TenantAccess.Only(tenants));
        }

        return ValueTask.FromResult(user.HasClaim(OperationPermissions.ClaimType, AllTenants) ? TenantAccess.AllTenants : TenantAccess.Only([]));
    }
}
