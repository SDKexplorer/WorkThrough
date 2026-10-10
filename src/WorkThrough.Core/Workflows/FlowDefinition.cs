using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough;

public sealed record InputBinding(string TargetProperty, string? SourceStep, string SourceProperty);

public sealed record FlowCondition(string SourceStep, string Property, JsonElement Expected);

public sealed record FlowStep(
    string Id, JobContractId Contract, JsonElement Input, string Queue,
    string[] After, InputBinding[] Bindings, FlowCondition? Condition, int MaxAttempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SignalWait? Signal = null)
{
    /// <summary>Optional saved display label. Dependencies and dispatch continue to use permanent identities.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }
}

public enum FlowFailurePolicy
{
    Stop, Suspend
}

/// <summary>Run lifecycle. Persisted and archived by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlowStatus>))]
public enum FlowStatus
{
    Running, Paused, Incident, Succeeded, Failed
}

public sealed record FlowDefinition(string Key, int Version, FlowStep[] Steps, FlowFailurePolicy FailurePolicy = FlowFailurePolicy.Stop);

public sealed record WorkflowRun(Guid Id, string FlowKey, int Version, string? CorrelationId, FlowStatus Status, string? Error);
