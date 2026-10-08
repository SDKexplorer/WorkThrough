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
        if (displayName is { Length: > 200 })
        {
            throw new ArgumentException("Display names are limited to 200 characters.", nameof(displayName));
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

/// <summary>Opt out of generated registration when the composition root supplies the permanent identity.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ManualProcessorRegistrationAttribute : Attribute;

/// <summary>Declares a compatible historical address. Activation requires a coordinated registry update.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ProcessorAliasAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public int Version { get; set; } = 1;
}
