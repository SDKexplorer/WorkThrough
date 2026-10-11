using System.Text.Json;

namespace WorkThrough;

/// <summary>A producer-facing typed address. No implementation assembly is required.</summary>
public sealed record JobDefinition<TInput>
{
    public JobDefinition(string key, int version = 1, string? displayName = null)
        : this(new JobContractId(key, version), displayName)
    {
    }

    public JobDefinition(JobContractId identity, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (displayName is { Length: > FlowValidator.MaximumDisplayNameLength })
        {
            throw new ArgumentException($"Display names are limited to {FlowValidator.MaximumDisplayNameLength} characters.", nameof(displayName));
        }

        Identity = identity;
        DisplayName = displayName;
    }

    public JobContractId Identity { get; }
    public string? DisplayName { get; }

    /// <summary>Deserializes a saved compatibility fixture using the same wire settings as execution.</summary>
    public TInput ReadInput(JsonElement payload) => payload.Deserialize<TInput>(JobJson.Options)
        ?? throw new JsonException($"Null input is invalid for {Identity}.");
}
