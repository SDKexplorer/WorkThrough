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
});
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkThroughMcp();
await app.RunAsync();

internal sealed class McpSettings
{
    public required Uri Authority { get; init; }
    public required Uri Resource { get; init; }
    public required string Environment { get; init; }
    public string EndpointPath { get; init; } = "/operations/mcp";
}
