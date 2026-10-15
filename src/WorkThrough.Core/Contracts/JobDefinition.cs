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

    /// <summary>The queue new work at this address uses unless a producer chooses another.</summary>
    public string Queue { get; private init; } = JobDefaults.Queue;

    /// <summary>The attempts new work at this address gets unless a producer chooses otherwise.</summary>
    public int MaxAttempts { get; private init; } = JobDefaults.MaxAttempts;

    /// <summary>This address with <paramref name="queue"/> as the default queue for new work.</summary>
    public JobDefinition<TInput> InQueue(string queue) => string.IsNullOrWhiteSpace(queue)
        ? throw new ArgumentException("Queue must be non-empty.", nameof(queue))
        : this with { Queue = queue };

    /// <summary>This address with <paramref name="attempts"/> as the default attempt budget for new work.</summary>
    public JobDefinition<TInput> WithMaxAttempts(int attempts) => attempts < 1
        ? throw new ArgumentOutOfRangeException(nameof(attempts), "Jobs need at least one attempt.")
        : this with { MaxAttempts = attempts };

    /// <summary>Options carrying this address's defaults.</summary>
    public EnqueueOptions DefaultOptions => new(Queue: Queue, MaxAttempts: MaxAttempts);

    /// <summary>Deserializes a saved compatibility fixture using the same wire settings as execution.</summary>
    public TInput ReadInput(JsonElement payload) => JobJson.Deserialize<TInput>(payload)
        ?? throw new JsonException($"Null input is invalid for {Identity}.");
}
