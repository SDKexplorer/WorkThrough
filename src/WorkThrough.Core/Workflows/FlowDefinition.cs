using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough;

public sealed record InputBinding(string TargetProperty, string? SourceStep, string SourceProperty);

public sealed record FlowCondition(string SourceStep, string Property, JsonElement Expected);

/// <summary>
/// Runs a published flow as one step. The step's bound input is the child run's input. The step succeeds when the child succeeds,
/// with <c>{"stepId": output}</c> for each of the child's final steps, and fails when the child fails. Like a signal wait, it holds no
/// worker while the child runs.
/// </summary>
public sealed record FlowChild(string FlowKey, int FlowVersion, TimeSpan Timeout)
{
    /// <summary>The built-in address of child steps. No worker executes it.</summary>
    public static JobContractId Processor { get; } = new("workthrough.child-flow", 1);

    /// <summary>The deepest nesting of child runs, which stops a flow from starting itself without end.</summary>
    public const int MaximumDepth = 8;

    /// <summary>The longest a child step waits for its run, the longest supported delay.</summary>
    public static TimeSpan MaximumTimeout { get; } = TimeSpan.FromDays(365);

    /// <summary>The parent-run signal that carries a child run's outcome.</summary>
    public static string SignalName(Guid childRun) => $"child:{childRun:N}";

    /// <summary>
    /// The child definition when the parent was built in code. Not saved: starting the parent publishes it if the store lacks it, so
    /// only its key and version persist.
    /// </summary>
    [JsonIgnore]
    public FlowDefinition? Definition { get; init; }

    /// <summary>True for signals that carry a child outcome rather than external input.</summary>
    public static bool IsChildSignal(string name) => name.StartsWith("child:", StringComparison.Ordinal);
}

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

    /// <summary>Runs another flow instead of a job; see <see cref="FlowChild"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowChild? Child { get; init; }
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
    public int RequiredProtocol => Steps.Any(step => step.Child is not null) ? EngineProtocol.ChildFlows
        : Steps.Any(step => step.ForEach is not null) ? EngineProtocol.FanOut
        : EngineProtocol.Baseline;
}

public sealed record WorkflowRun(Guid Id, string FlowKey, int Version, string? CorrelationId, FlowStatus Status, string? Error)
{
    /// <summary>The run whose child step started this run, if any.</summary>
    public Guid? ParentRunId { get; init; }

    /// <summary>The parent's step job that waits for this run, if any.</summary>
    public Guid? ParentJobId { get; init; }
}
