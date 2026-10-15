using System.Security.Claims;
using Xunit;

namespace WorkThrough.Tests;

public sealed class RolePermissionsTests
{
    private const string RoleClaim = "roles";

    [Fact]
    public void Mapped_roles_grant_their_permissions_once_and_unmapped_roles_grant_nothing()
    {
        var permissions = new RolePermissions(new Dictionary<string, string[]>
        {
            ["viewer"] = [OperationPermissions.ReadExecutions],
            ["operator"] = [OperationPermissions.ReadExecutions, OperationPermissions.RetryJobs, TenantClaimResolver.AllTenants]
        });
        var identity = new ClaimsIdentity([new(RoleClaim, "viewer"), new(RoleClaim, "operator"), new(RoleClaim, "stranger")], "test");
        permissions.Grant(identity, RoleClaim);
        var granted = identity.FindAll(OperationPermissions.ClaimType).ToArray();
        Assert.Equal([OperationPermissions.ReadExecutions, OperationPermissions.RetryJobs, TenantClaimResolver.AllTenants], granted.Select(claim => claim.Value));
        Assert.All(granted, claim => Assert.Equal(RolePermissions.Issuer, claim.Issuer));
    }

    [Fact]
    public void A_misspelled_permission_fails_when_the_map_is_built()
    {
        var invalid = Assert.Throws<ArgumentException>(() => new RolePermissions(new Dictionary<string, string[]> { ["operator"] = ["jobs.retyr"] }));
        Assert.Contains("jobs.retyr", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_claims_limit_access_all_tenants_widens_it_and_neither_denies_it()
    {
        var resolver = new TenantClaimResolver();
        var scoped = await resolver.ResolveAsync(Principal(new Claim(TenantClaimResolver.DefaultClaimType, "acme")), CancellationToken.None);
        var all = await resolver.ResolveAsync(Principal(new Claim(OperationPermissions.ClaimType, TenantClaimResolver.AllTenants)), CancellationToken.None);
        var none = await resolver.ResolveAsync(Principal(), CancellationToken.None);
        Assert.Equal(["acme"], scoped.Tenants!);
        Assert.False(all.IsRestricted);
        Assert.Empty(none.Tenants!);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
}
