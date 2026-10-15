using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;

namespace WorkThrough;

/// <summary>Optional authenticated Streamable HTTP transport; does not register workers, commands or storage initialization.</summary>
public static class McpHostingExtensions
{
    public const string AuthenticationScheme = "WorkThrough.Mcp.Bearer";
    private const string EndpointPolicy = "WorkThrough.Mcp.Access";
    private const string ServerName = "WorkThrough";
    private const string BearerHeaderMethod = "header";
    private static readonly string ServerVersion = typeof(McpHostingExtensions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    private static readonly TimeSpan TokenClockSkew = TimeSpan.FromSeconds(30);
    private static readonly string[] ReadPermissions =
        [OperationPermissions.ReadExecutions, OperationPermissions.ReadPayloads, OperationPermissions.ReadAudit, OperationPermissions.ReadRetention];

    /// <summary>Registers one MCP server. Configure the store separately; JWT customization supports host signing-key configuration.</summary>
    public static IServiceCollection AddWorkThroughMcp(this IServiceCollection services, WorkThroughMcpOptions options,
        Action<JwtBearerOptions>? configureJwt = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<McpHttpGuard>();
        services.AddScoped<McpToolGuard>();
        services.AddScoped<McpTenantScope>();
        services.TryAddSingleton<IMcpAccessSink, LoggingMcpAccessSink>();
        services.TryAddSingleton<IMcpDiagnosticRedactor, StoredDiagnosticRedactor>();
        services.AddAuthentication().AddJwtBearer(AuthenticationScheme, jwt =>
        {
            jwt.Authority = options.Authority.AbsoluteUri;
            configureJwt?.Invoke(jwt);
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.SaveToken = false;
            jwt.TokenValidationParameters.ValidateIssuer = true;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidAudience = options.Resource.AbsoluteUri;
            jwt.TokenValidationParameters.ValidAudiences = null;
            jwt.TokenValidationParameters.ValidateLifetime = true;
            jwt.TokenValidationParameters.RequireExpirationTime = true;
            jwt.TokenValidationParameters.RequireSignedTokens = true;
            jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;
            jwt.TokenValidationParameters.ClockSkew = TokenClockSkew;
            jwt.Events ??= new JwtBearerEvents();
            jwt.Events.OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{options.MetadataUri.AbsoluteUri}\"";

                return Task.CompletedTask;
            };
        });
        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(EndpointPolicy, policy => policy.AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim(McpClaims.Subject).RequireClaim(McpClaims.Issuer));
            foreach (var permission in ReadPermissions.Concat(options.Actions.Select(ActionTools.Permission)).Distinct())
            {
                var name = OperationPermissions.Policy(permission);
                if (authorization.GetPolicy(name) is null)
                {
                    authorization.AddPolicy(name, policy => policy.RequireAuthenticatedUser().RequireClaim(OperationPermissions.ClaimType, permission));
                }
            }
        });
        var toolJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter() }
        };
        services.AddMcpServer(server => server.ServerInfo = new() { Name = ServerName, Version = ServerVersion })
            .WithHttpTransport(http => http.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<ExecutionTools>(toolJson)
            .WithTools<InspectionTools>(toolJson)
            .WithTools<HistoryTools>(toolJson)
            .WithTools<RuntimeTools>(toolJson)
            .WithTools<RetentionTools>(toolJson)
            .WithTools<ActionTools>(toolJson)
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (context, ct) =>
                {
                    var guard = context.Services!.GetRequiredService<McpToolGuard>();
                    if (!await guard.AuthorizeAsync(context.User))
                    {
                        return new() { Tools = [] };
                    }

                    var result = await next(context, ct);
                    var visible = new List<ModelContextProtocol.Protocol.Tool>();
                    foreach (var tool in result.Tools.Where(tool => ExecutionTools.Names.Contains(tool.Name, StringComparer.Ordinal)))
                    {
                        if (!McpCapabilities.IsAvailable(context.Services!, tool.Name))
                        {
                            continue;
                        }

                        if (await guard.AuthorizeAsync(context.User, tool.Name))
                        {
                            visible.Add(tool);
                        }
                    }

                    result.Tools = visible;

                    return result;
                });
                filters.AddCallToolFilter(next => (context, ct) =>
                    context.Services!.GetRequiredService<McpToolGuard>().InvokeAsync(context, next, ct));
            });

        return services;
    }

    /// <summary>Maps MCP and RFC 9728 discovery. Host authentication/authorization middleware must precede endpoints.</summary>
    public static IEndpointConventionBuilder MapWorkThroughMcp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<WorkThroughMcpOptions>();
        endpoints.MapGet(options.MetadataPath, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            return Results.Json(new
            {
                resource = options.Resource.AbsoluteUri,
                authorization_servers = new[] { options.Authority.AbsoluteUri },
                bearer_methods_supported = new[] { BearerHeaderMethod }
            });
        }).AllowAnonymous();
        var route = endpoints.MapMcp(options.EndpointPath).RequireAuthorization(EndpointPolicy);
        route.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate!;
            endpoint.RequestDelegate = context => context.RequestServices.GetRequiredService<McpHttpGuard>().InvokeAsync(context, next);
        });

        return route;
    }
}
