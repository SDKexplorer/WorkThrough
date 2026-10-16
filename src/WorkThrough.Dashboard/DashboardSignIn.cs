using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

/// <summary>
/// OpenID Connect sign-in for the console: the provider's login page, a short session cookie, and roles mapped to permissions by the host.
/// Bind it from configuration, or start from <see cref="EntraId"/>, <see cref="Auth0"/> or <see cref="Keycloak"/>.
/// </summary>
public sealed class DashboardSignInOptions
{
    public const string SessionCookie = "__Host-WorkThrough.Session";

    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>The ID-token claim holding the user's roles: "roles" for Entra ID and Keycloak, a namespaced claim for Auth0.</summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>The ID-token claim holding the user's tenants. Null leaves every signed-in user store-wide; see <see cref="TenantClaimResolver"/>.</summary>
    public string? TenantClaim { get; set; }

    /// <summary>Role name to the permissions it grants; see <see cref="WorkThrough.RolePermissions"/>.</summary>
    public Dictionary<string, string[]> RolePermissions { get; set; } = [];

    /// <summary>The path the console is mapped at. Its API answers 401 instead of redirecting to the provider.</summary>
    public string ConsolePath { get; set; } = "/operations";

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Allows a provider over plain HTTP, for a local provider in development only.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Microsoft Entra ID with app roles in the <c>roles</c> claim. Entra has no tenant claim of its own; set <see cref="TenantClaim"/> to one you issue.</summary>
    public static DashboardSignInOptions EntraId(string directoryTenantId, string clientId, string clientSecret) => new()
    {
        Authority = $"https://login.microsoftonline.com/{directoryTenantId}/v2.0",
        ClientId = clientId,
        ClientSecret = clientSecret,
        RoleClaim = "roles"
    };

    /// <summary>
    /// Auth0 with a post-login Action that sets <c>{claimNamespace}roles</c> and <c>{claimNamespace}tenant</c>, for example
    /// <c>https://workthrough.example/</c>: Auth0 keeps unnamespaced custom claims out of ID tokens.
    /// </summary>
    public static DashboardSignInOptions Auth0(string domain, string clientId, string clientSecret, string claimNamespace) => new()
    {
        Authority = $"https://{domain}/",
        ClientId = clientId,
        ClientSecret = clientSecret,
        RoleClaim = claimNamespace + "roles",
        TenantClaim = claimNamespace + "tenant"
    };

    /// <summary>Keycloak with a realm roles mapper to <c>roles</c> and a user-attribute mapper to <c>tenant</c>, as in tests/Oidc.</summary>
    public static DashboardSignInOptions Keycloak(string baseUrl, string realm, string clientId, string clientSecret) => new()
    {
        Authority = $"{baseUrl.TrimEnd('/')}/realms/{realm}",
        ClientId = clientId,
        ClientSecret = clientSecret,
        RoleClaim = "roles",
        TenantClaim = TenantClaimResolver.DefaultClaimType
    };
}

public static class DashboardSignIn
{
    private const string NameClaim = "name";

    /// <summary>
    /// Signs console users in through <paramref name="options"/>' provider and grants the permissions their roles map to. Call it with
    /// <see cref="DashboardEndpoints.AddWorkThroughDashboard"/>, then <c>UseAuthentication</c> and <c>UseAuthorization</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The authority, client ID or client secret is missing.</exception>
    /// <exception cref="ArgumentException">A role grants a permission WorkThrough does not check.</exception>
    public static AuthenticationBuilder AddWorkThroughSignIn(this IServiceCollection services, DashboardSignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Authority is null || options.ClientId is null || options.ClientSecret is null)
        {
            throw new InvalidOperationException("Sign-in needs an authority, client ID and client secret; keep the secret in a secret store.");
        }

        var permissions = new RolePermissions(options.RolePermissions);
        if (options.TenantClaim is { } tenantClaim)
        {
            services.AddSingleton<ITenantAccessResolver>(new TenantClaimResolver(tenantClaim));
        }

        return services.AddAuthentication(authentication =>
        {
            authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authentication.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddCookie(cookie => ConfigureSession(cookie, options)).AddOpenIdConnect(oidc => ConfigureProvider(oidc, options, permissions));
    }

    /// <summary>Signs the user out of the session and the provider. The console posts here with its antiforgery token.</summary>
    public static RouteHandlerBuilder MapWorkThroughSignOut(this IEndpointRouteBuilder endpoints, string path = "/logout") =>
        endpoints.MapPost(path, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();

    private static void ConfigureSession(CookieAuthenticationOptions cookie, DashboardSignInOptions options)
    {
        cookie.Cookie.Name = DashboardSignInOptions.SessionCookie;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.HttpOnly = true;
        cookie.ExpireTimeSpan = options.SessionLifetime;
        cookie.SlidingExpiration = false;
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            return Task.CompletedTask;
        };
    }

    private static void ConfigureProvider(OpenIdConnectOptions oidc, DashboardSignInOptions options, RolePermissions permissions)
    {
        oidc.Authority = options.Authority;
        oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
        oidc.ClientId = options.ClientId;
        oidc.ClientSecret = options.ClientSecret;
        oidc.ResponseType = "code";
        oidc.UsePkce = true;
        oidc.MapInboundClaims = false;
        oidc.SaveTokens = false;
        oidc.TokenValidationParameters.NameClaimType = NameClaim;
        oidc.TokenValidationParameters.RoleClaimType = options.RoleClaim;
        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Request.Path.StartsWithSegments(options.ConsolePath + "/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.HandleResponse();
            }

            return Task.CompletedTask;
        };
        oidc.Events.OnTokenValidated = context =>
        {
            permissions.Grant((ClaimsIdentity)context.Principal!.Identity!, options.RoleClaim);

            return Task.CompletedTask;
        };
    }
}
