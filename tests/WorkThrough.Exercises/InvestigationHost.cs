using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace WorkThrough.Exercises;

internal sealed class InvestigationHost(WebApplication app, RSA key, string address, TimeSpan lifetime) : IAsyncDisposable
{
    internal const int ConcurrentRequests = 32;
    internal const int RequestsPerMinute = 10000;
    private const string Authority = "https://qualification.workthrough.invalid";
    private static readonly Uri Resource = new("http://127.0.0.1/qualification/mcp");

    internal static async Task<InvestigationHost> StartAsync(ExerciseDatabase database, TimeSpan lifetime)
    {
        var key = RSA.Create(2048);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IExecutionQueries>(database.Store);
        builder.Services.AddSingleton<IExecutionInspectionQueries>(database.Store);
        builder.Services.AddSingleton<IOperationHistoryQueries>(database.Store);
        builder.Services.AddSingleton<IRuntimeQueries>(database.Store);
        builder.Services.AddSingleton<IRetentionQueries>(database.Store);
        builder.Services.AddSingleton<IMcpAccessSink, CountingAccessSink>();
        builder.Services.AddWorkThroughMcp(new()
        {
            Authority = new(Authority),
            Resource = Resource,
            EndpointPath = Resource.AbsolutePath,
            Environment = "disposable-qualification",
            AllowLoopbackHttp = true,
            ConcurrentRequests = ConcurrentRequests,
            RequestsPerMinute = RequestsPerMinute
        }, jwt =>
        {
            jwt.Configuration = new OpenIdConnectConfiguration { Issuer = Authority };
            jwt.Configuration.SigningKeys.Add(new RsaSecurityKey(key));
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkThroughMcp();
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            return new(app, key, address, lifetime);
        }
        catch
        {
            await app.DisposeAsync();
            key.Dispose();
            throw;
        }
    }

    internal async Task<InvestigationClient> ConnectAsync(int index, CancellationToken ct)
    {
        var token = new JwtSecurityToken(Authority, Resource.AbsoluteUri,
            [new("sub", $"investigator-{index}"), new Claim(OperationPermissions.ClaimType, OperationPermissions.ReadExecutions),
                new Claim(OperationPermissions.ClaimType, OperationPermissions.ReadAudit),
                new Claim(OperationPermissions.ClaimType, OperationPermissions.ReadRetention)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.Add(lifetime).AddMinutes(10),
            new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        try
        {
            var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new(address + Resource.AbsolutePath),
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false
            }, http), cancellationToken: ct);

            return new(client, http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    internal long AccessEvents => ((CountingAccessSink)app.Services.GetRequiredService<IMcpAccessSink>()).Count;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        key.Dispose();
    }

    private sealed class CountingAccessSink : IMcpAccessSink
    {
        private long count;
        internal long Count => Interlocked.Read(ref count);
        public ValueTask WriteAsync(McpAccessEvent access, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref count);

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class InvestigationClient(McpClient client, HttpClient http) : IAsyncDisposable
{
    internal McpClient Client => client;
    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        http.Dispose();
    }
}
