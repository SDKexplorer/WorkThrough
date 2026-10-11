using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough;

public sealed record InputBinding(string TargetProperty, string? SourceStep, string SourceProperty);

public sealed record FlowCondition(string SourceStep, string Property, JsonElement Expected);

/// <summary>
/// Runs a step once per element of an array in an earlier step's output. Each item step receives its element in
/// <paramref name="TargetProperty"/>; the step succeeds with <c>{"items":[...]}</c>, the item outputs in order, after every item succeeds.
/// </summary>
public sealed record FlowFanOut(string SourceStep, string SourceProperty, string TargetProperty)
{
    /// <summary>The most item steps one fan-out may create. A larger array fails the step.</summary>
    public const int MaximumItems = 1000;

    /// <summary>The output property that holds the item outputs.</summary>
    public const string ItemsProperty = "items";

    /// <summary>The step ID of item <paramref name="index"/> of fan-out step <paramref name="stepId"/>.</summary>
    public static string ItemStepId(string stepId, int index) => $"{stepId}[{index}]";
}

public sealed record FlowStep(
    string Id, JobContractId Contract, JsonElement Input, string Queue,
    string[] After, InputBinding[] Bindings, FlowCondition? Condition, int MaxAttempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SignalWait? Signal = null)
{
    /// <summary>Optional saved display label. Dependencies and dispatch continue to use permanent identities.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    /// <summary>Expands the step into one item step per array element; see <see cref="FlowFanOut"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowFanOut? ForEach { get; init; }
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

public sealed record FlowDefinition(string Key, int Version, FlowStep[] Steps, FlowFailurePolicy FailurePolicy = FlowFailurePolicy.Stop)
{
    /// <summary>The engine protocol every host must understand before a run of this definition may start.</summary>
    [JsonIgnore]
    public int RequiredProtocol => Steps.Any(step => step.ForEach is not null) ? EngineProtocol.FanOut : EngineProtocol.Baseline;
}

public sealed record WorkflowRun(Guid Id, string FlowKey, int Version, string? CorrelationId, FlowStatus Status, string? Error);
