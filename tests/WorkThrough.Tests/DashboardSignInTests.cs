using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace WorkThrough.Tests;

public sealed class DashboardSignInTests
{
    private static DashboardSignInOptions Options(params (string Role, string[] Permissions)[] roles) =>
        new()
        {
            Authority = "https://login.example/realms/workthrough",
            ClientId = "console",
            ClientSecret = "secret",
            TenantClaim = TenantClaimResolver.DefaultClaimType,
            RolePermissions = roles.ToDictionary(role => role.Role, role => role.Permissions)
        };

    [Fact]
    public async Task A_validated_id_token_gains_the_permissions_its_roles_map_to_and_tenants_scope_it()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddWorkThroughSignIn(Options(("workthrough-operator", [OperationPermissions.ReadExecutions, OperationPermissions.RetryJobs])));
        await using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.True(oidc.UsePkce);
        Assert.False(oidc.MapInboundClaims);
        Assert.Equal(OpenIdConnectResponseType.Code, oidc.ResponseType);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("roles", "workthrough-operator"), new("roles", "unmapped"),
            new(TenantClaimResolver.DefaultClaimType, "acme")], "oidc"));
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        var validated = new TokenValidatedContext(new DefaultHttpContext { RequestServices = provider }, scheme, oidc, principal, new());
        await oidc.Events.TokenValidated(validated);
        Assert.Equal([OperationPermissions.ReadExecutions, OperationPermissions.RetryJobs],
            principal.FindAll(OperationPermissions.ClaimType).Select(claim => claim.Value));
        var access = await provider.GetRequiredService<ITenantAccessResolver>().ResolveAsync(principal, CancellationToken.None);
        Assert.Equal(["acme"], access.Tenants!);
    }

    [Fact]
    public void Missing_credentials_and_misspelled_permissions_fail_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkThroughSignIn(new DashboardSignInOptions()));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddWorkThroughSignIn(Options(("operator", ["jobs.retyr"]))));
    }

    [Fact]
    public void Provider_presets_name_the_authority_and_claims_each_provider_issues()
    {
        var entra = DashboardSignInOptions.EntraId("contoso-tenant", "client", "secret");
        var auth0 = DashboardSignInOptions.Auth0("contoso.eu.auth0.com", "client", "secret", "https://workthrough.example/");
        var keycloak = DashboardSignInOptions.Keycloak("https://id.example/", "ops", "client", "secret");
        Assert.Equal(("https://login.microsoftonline.com/contoso-tenant/v2.0", "roles", null), (entra.Authority, entra.RoleClaim, entra.TenantClaim));
        Assert.Equal(("https://contoso.eu.auth0.com/", "https://workthrough.example/roles", "https://workthrough.example/tenant"),
            (auth0.Authority, auth0.RoleClaim, auth0.TenantClaim));
        Assert.Equal(("https://id.example/realms/ops", "roles", "tenant"), (keycloak.Authority, keycloak.RoleClaim, keycloak.TenantClaim));
    }
}
