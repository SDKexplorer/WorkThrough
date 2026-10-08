using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using WorkThrough;
using WorkThrough.Storage;

internal static class McpPackageSmoke
{
    internal static async Task RunAsync(string database, Guid executionId, Guid runId)
    {
        using var key = RSA.Create(2048);
        const string authority = "https://package-test.example";
        // Public resource identity stays fixed while this smoke test uses an ephemeral internal listener.
        var resource = new Uri("http://127.0.0.1/operations/mcp");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddWorkThrough().UseSqlite($"Data Source={database};Pooling=False").InitializeStorageOnStart(false);
        builder.Services.AddWorkThroughMcp(new()
        {
            Resource = resource,
            Authority = new(authority),
            Environment = "package-smoke",
            EndpointPath = resource.AbsolutePath,
            AllowLoopbackHttp = true
        }, jwt =>
        {
            jwt.Configuration = new OpenIdConnectConfiguration { Issuer = authority };
            jwt.Configuration.SigningKeys.Add(new RsaSecurityKey(key));
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkThroughMcp();
        await app.StartAsync();
        try
        {
            var token = new JwtSecurityToken(authority, resource.AbsoluteUri,
                [new("sub", "package-consumer"), new Claim(OperationPermissions.ClaimType, OperationPermissions.ReadExecutions),
                    new Claim(OperationPermissions.ClaimType, OperationPermissions.ReadPayloads)],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1),
                new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new(address + resource.AbsolutePath),
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false
            }, http));
            var response = await client.CallToolAsync("workthrough_get_execution", new Dictionary<string, object?> { ["id"] = executionId });
            if (response.IsError == true || response.StructuredContent!.Value.GetProperty("execution").GetProperty("id").GetGuid() != executionId)
            {
                throw new InvalidOperationException("Packaged MCP did not return the saved execution.");
            }

            var flow = await client.CallToolAsync("workthrough_get_flow", new Dictionary<string, object?> { ["runId"] = runId });
            if (flow.IsError == true || flow.StructuredContent!.Value.GetProperty("nodes").GetArrayLength() != 2
                || flow.StructuredContent.Value.GetProperty("dependencies").GetArrayLength() != 1)
            {
                throw new InvalidOperationException("Packaged MCP did not return the saved flow graph.");
            }

            var diagnostic = await client.CallToolAsync("workthrough_get_diagnostics", new Dictionary<string, object?> { ["id"] = executionId, ["attempt"] = 1 });
            if (diagnostic.IsError == true || !diagnostic.StructuredContent!.Value.GetProperty("diagnostic").GetProperty("attemptExists").GetBoolean())
            {
                throw new InvalidOperationException("Packaged MCP did not inspect the selected attempt.");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
