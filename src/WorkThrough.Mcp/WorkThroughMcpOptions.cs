namespace WorkThrough;

/// <summary>One protected MCP endpoint over the host's configured execution store, read-only unless the host enrolls actions.</summary>
public sealed class WorkThroughMcpOptions
{
    /// <summary>Public HTTPS endpoint URI. Tokens must use this exact resource as their audience.</summary>
    public required Uri Resource { get; init; }
    /// <summary>Trusted HTTPS OpenID Connect authority used for issuer and signing-key discovery.</summary>
    public required Uri Authority { get; init; }
    /// <summary>Deployment label, not a tenant identifier or database connection string.</summary>
    public required string Environment { get; init; }
    public string EndpointPath { get; init; } = "/mcp";
    public int ConcurrentRequests { get; init; } = 16;
    public int RequestsPerMinute { get; init; } = 120;
    /// <summary>Only permits HTTP for a loopback resource URI, for local development/testing.</summary>
    public bool AllowLoopbackHttp { get; init; }

    /// <summary>
    /// Actions the endpoint may propose and execute after a person approves them through a registered <see cref="IMcpActionApprovals"/>.
    /// Empty by default: the endpoint is read-only.
    /// </summary>
    public IReadOnlySet<McpAction> Actions { get; init; } = new HashSet<McpAction>();

    /// <summary>How long a prepared proposal can be approved and executed.</summary>
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(15);

    internal static readonly TimeSpan MaximumApprovalLifetime = TimeSpan.FromHours(24);

    internal string MetadataPath => "/.well-known/oauth-protected-resource" + Resource.AbsolutePath.TrimEnd('/');
    internal Uri MetadataUri => new(Resource.GetLeftPart(UriPartial.Authority) + MetadataPath);

    internal void Validate()
    {
        if (!Resource.IsAbsoluteUri || !Authority.IsAbsoluteUri || Authority.Scheme != Uri.UriSchemeHttps
            || Resource.Scheme != Uri.UriSchemeHttps && !(AllowLoopbackHttp && Resource.IsLoopback && Resource.Scheme == Uri.UriSchemeHttp)
            || Resource.Query.Length != 0 || Resource.Fragment.Length != 0 || Resource.UserInfo.Length != 0
            || Authority.Query.Length != 0 || Authority.Fragment.Length != 0 || Authority.UserInfo.Length != 0)
        {
            throw new ArgumentException("MCP requires absolute HTTPS resource and authority URIs without credentials, query or fragment.");
        }

        if (string.IsNullOrWhiteSpace(Environment) || Environment.Length > 128
            || !EndpointPath.StartsWith('/') || EndpointPath.Length > 256 || EndpointPath == "/"
            || EndpointPath.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_'))
            || ConcurrentRequests is < 1 or > 256 || RequestsPerMinute is < 1 or > 10000
            || Actions is null || ApprovalLifetime <= TimeSpan.Zero || ApprovalLifetime > MaximumApprovalLifetime)
        {
            throw new ArgumentException("Invalid MCP environment, endpoint path, request limits or approval lifetime.");
        }
    }
}
