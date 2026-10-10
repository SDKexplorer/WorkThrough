namespace WorkThrough;

/// <summary>Token claims the MCP endpoint relies on. Inbound claim mapping is disabled, so these are JWT names.</summary>
internal static class McpClaims
{
    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string ClientId = "client_id";
    public const string AuthorizedParty = "azp";
}
