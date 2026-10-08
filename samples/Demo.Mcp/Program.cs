using WorkThrough;
using WorkThrough.Storage;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Operations").Get<OperationsSettings>()
    ?? throw new InvalidOperationException("Configure Operations:ConnectionString, Authority, Resource and Environment before starting.");
builder.Services.AddWorkThrough().UseSqlite(settings.ConnectionString).InitializeStorageOnStart(false);
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

internal sealed class OperationsSettings
{
    public required string ConnectionString { get; init; }
    public required Uri Authority { get; init; }
    public required Uri Resource { get; init; }
    public required string Environment { get; init; }
    public string EndpointPath { get; init; } = "/operations/mcp";
}
