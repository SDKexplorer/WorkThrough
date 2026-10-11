using System.Security.Claims;
using WorkThrough;

namespace Shop;

/// <summary>
/// Operators without a tenant claim see the whole store; users carrying "tenant" claims, issued by the identity provider, see
/// only those tenants' executions in the dashboard and MCP. Never derive tenants from request input.
/// </summary>
public sealed class TenantClaimResolver : ITenantAccessResolver
{
    public const string ClaimType = "tenant";

    public ValueTask<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tenants = user.FindAll(ClaimType).Select(claim => claim.Value).ToArray();

        return ValueTask.FromResult(tenants.Length == 0 ? TenantAccess.AllTenants : TenantAccess.Only(tenants));
    }
}
