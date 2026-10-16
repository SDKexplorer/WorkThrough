using System.Security.Claims;

namespace WorkThrough;

/// <summary>
/// Grants WorkThrough permissions from the roles an identity provider issues. Call <see cref="Grant"/> when a token is validated, for
/// the console in OpenID Connect's <c>OnTokenValidated</c> and for MCP in the JWT bearer one. A role the map does not name grants nothing.
/// </summary>
public sealed class RolePermissions
{
    /// <summary>The issuer of granted permission claims, so they are distinguishable from claims the provider issued.</summary>
    public const string Issuer = "WorkThroughHost";

    private readonly Dictionary<string, string[]> map;

    /// <param name="map">Role name to the permissions it grants: <see cref="OperationPermissions.All"/> or <see cref="TenantClaimResolver.AllTenants"/>.</param>
    /// <exception cref="ArgumentException">A permission is not one WorkThrough checks, which is usually a typo.</exception>
    public RolePermissions(IReadOnlyDictionary<string, string[]> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var unknown = map.SelectMany(role => role.Value).Where(permission => !IsKnown(permission)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown WorkThrough permissions: {string.Join(", ", unknown)}.", nameof(map));
        }

        this.map = map.ToDictionary(role => role.Key, role => role.Value.Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Adds a permission claim for each permission the caller's <paramref name="roleClaim"/> values grant.</summary>
    public void Grant(ClaimsIdentity identity, string roleClaim)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var granted = identity.FindAll(roleClaim)
            .SelectMany(role => map.TryGetValue(role.Value, out var permissions) ? permissions : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var permission in granted)
        {
            identity.AddClaim(new(OperationPermissions.ClaimType, permission, ClaimValueTypes.String, Issuer));
        }
    }

    private static bool IsKnown(string permission) => permission == TenantClaimResolver.AllTenants || OperationPermissions.All.Contains(permission);
}
