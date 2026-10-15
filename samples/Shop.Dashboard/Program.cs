using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Shop;
using WorkThrough;

// An operations console with no job code: it reads the shared store and needs neither workers nor job classes.
const string ConsolePath = "/operations";
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkThrough().UseShopStorage(ShopSetup.Settings(builder.Configuration)).InitializeStorageOnStart(false);
builder.Services.AddWorkThroughDashboard();

var identity = builder.Configuration.GetSection("Identity").Get<DashboardIdentityOptions>();
var anonymousDevelopment = builder.Environment.IsDevelopment() && identity?.Authority is null;
if (!anonymousDevelopment)
{
    if (identity?.Authority is null || identity.ClientId is null || identity.ClientSecret is null)
    {
        throw new InvalidOperationException("Configure Identity:Authority, ClientId, and ClientSecret using host configuration or a secret store.");
    }

    // Users whose ID token carries tenant claims see only those tenants; tenants.all sees the store; anyone else sees nothing.
    builder.Services.AddSingleton<ITenantAccessResolver>(new TenantClaimResolver(identity.TenantClaim));
    var permissions = new RolePermissions(identity.RolePermissions);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    }).AddCookie(options =>
        {
            options.Cookie.Name = "__Host-WorkThrough.Session";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;

                return Task.CompletedTask;
            };
        }).AddOpenIdConnect(options =>
        {
            options.Authority = identity.Authority;

            // A local identity provider over plain HTTP is accepted only in Development, as in tests/Oidc.
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.ClientId = identity.ClientId;
            options.ClientSecret = identity.ClientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = identity.RoleClaim;
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                if (context.Request.Path.StartsWithSegments(ConsolePath + "/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.HandleResponse();
                }

                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                // Roles come from the validated ID token; their permission mapping is controlled by this host.
                permissions.Grant((ClaimsIdentity)context.Principal!.Identity!, identity.RoleClaim);

                return Task.CompletedTask;
            };
        });
}

var app = builder.Build();
if (!anonymousDevelopment)
{
    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);

        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }).RequireAuthorization();
}

app.MapWorkThroughDashboard(ConsolePath, options => options.AllowAnonymous = anonymousDevelopment);
app.MapGet("/", () => Results.Redirect(ConsolePath));
await app.RunAsync();

public sealed class DashboardIdentityOptions
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>The ID-token claim holding the user's roles: "roles" for Entra ID and Keycloak, a namespaced claim for Auth0.</summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>The ID-token claim holding the user's tenants; Auth0 needs a namespaced claim.</summary>
    public string TenantClaim { get; set; } = TenantClaimResolver.DefaultClaimType;

    public Dictionary<string, string[]> RolePermissions { get; set; } = [];
}
