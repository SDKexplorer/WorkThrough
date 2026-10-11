using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Shop;
using WorkThrough;

// An operations console with no processor code: it reads the shared store and needs neither workers nor handlers.
const string ConsolePath = "/operations";
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkThrough().UseShopStorage(ShopSetup.Settings(builder.Configuration)).InitializeStorageOnStart(false);
builder.Services.AddWorkThroughDashboard();

// Users whose identity token carries "tenant" claims see only those tenants; operators without one see the whole store.
builder.Services.AddSingleton<ITenantAccessResolver, TenantClaimResolver>();
var identity = builder.Configuration.GetSection("Identity").Get<DashboardIdentityOptions>();
var anonymousDevelopment = builder.Environment.IsDevelopment() && identity?.Authority is null;
if (!anonymousDevelopment)
{
    if (identity?.Authority is null || identity.ClientId is null || identity.ClientSecret is null)
    {
        throw new InvalidOperationException("Configure Identity:Authority, ClientId, and ClientSecret using host configuration or a secret store.");
    }

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
            options.ClientId = identity.ClientId;
            options.ClientSecret = identity.ClientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
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
                var claims = (ClaimsIdentity)context.Principal!.Identity!;
                // Roles come from the validated ID token; their permission mapping is controlled by this host.
                foreach (var role in identity.RolePermissions)
                {
                    if (context.Principal.IsInRole(role.Key))
                    {
                        foreach (var permission in role.Value.Where(DashboardPermissions.All.Contains))
                        {
                            claims.AddClaim(new(DashboardPermissions.ClaimType, permission, ClaimValueTypes.String, "WorkThroughHost"));
                        }
                    }
                }

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
    public Dictionary<string, string[]> RolePermissions { get; set; } = [];
}
