using Shop;
using WorkThrough;

// An operations console with no job code: it reads the shared store and needs neither workers nor job classes.
const string ConsolePath = "/operations";
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkThrough().UseShopStorage(ShopSetup.Settings(builder.Configuration)).InitializeStorageOnStart(false);
builder.Services.AddWorkThroughDashboard();

var identity = builder.Configuration.GetSection("Identity").Get<DashboardSignInOptions>();
var anonymousDevelopment = builder.Environment.IsDevelopment() && identity?.Authority is null;
if (!anonymousDevelopment)
{
    // Users whose ID token carries tenant claims see only those tenants; tenants.all sees the store; anyone else sees nothing.
    var signIn = identity ?? throw new InvalidOperationException("Configure Identity:Authority, ClientId, and ClientSecret using host configuration or a secret store.");
    signIn.ConsolePath = ConsolePath;

    // A local identity provider over plain HTTP is accepted only in Development, as in tests/Oidc.
    signIn.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    builder.Services.AddWorkThroughSignIn(signIn);
}

var app = builder.Build();
if (!anonymousDevelopment)
{
    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapWorkThroughSignOut();
}

app.MapWorkThroughDashboard(ConsolePath, options => options.AllowAnonymous = anonymousDevelopment);
app.MapGet("/", () => Results.Redirect(ConsolePath));
await app.RunAsync();
