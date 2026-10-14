using System.Security.Claims;
using Shop;
using WorkThrough;

// A read-only MCP endpoint for AI assistants. It needs an initialized store and a real authority: there is no anonymous mode.
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Mcp").Get<McpSettings>()
    ?? throw new InvalidOperationException("Configure Mcp:Authority, Mcp:Resource and Mcp:Environment before starting.");
builder.Services.AddWorkThrough().UseShopStorage(ShopSetup.Settings(builder.Configuration)).InitializeStorageOnStart(false);
builder.Services.AddSingleton<ITenantAccessResolver, TenantClaimResolver>();
builder.Services.AddWorkThroughMcp(new()
{
    Authority = settings.Authority,
    Resource = settings.Resource,
    Environment = settings.Environment,
    EndpointPath = settings.EndpointPath
}, jwt => jwt.Events = new()
{
    // Hosted providers issue roles (Entra ID "roles") or permissions (Auth0 "permissions"), not WorkThrough permissions. This host
    // decides what each one grants; nothing is taken from the token unmapped.
    OnTokenValidated = context =>
    {
        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        var granted = identity.FindAll(settings.RoleClaim).Select(claim => claim.Value)
            .SelectMany(role => settings.RolePermissions.TryGetValue(role, out var permissions) ? permissions : []).Distinct().ToArray();
        foreach (var permission in granted)
        {
            identity.AddClaim(new(OperationPermissions.ClaimType, permission, ClaimValueTypes.String, McpSettings.HostIssuer));
        }

        return Task.CompletedTask;
    }
});
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkThroughMcp();
await app.RunAsync();

internal sealed class McpSettings
{
    public const string HostIssuer = "WorkThroughHost";

    public required Uri Authority { get; init; }
    public required Uri Resource { get; init; }
    public required string Environment { get; init; }
    public string EndpointPath { get; init; } = "/operations/mcp";

    /// <summary>The token claim whose values <see cref="RolePermissions"/> maps: "roles" for Entra ID, "permissions" for Auth0.</summary>
    public string RoleClaim { get; init; } = "roles";

    public Dictionary<string, string[]> RolePermissions { get; init; } = [];
}
