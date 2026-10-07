using System.Text.Json;

namespace WorkThrough;

public sealed record InputBinding(string TargetProperty, string? SourceStep, string SourceProperty);
public sealed record FlowCondition(string SourceStep, string Property, JsonElement Expected);
public sealed record FlowStep(
    string Id, JobContractId Contract, JsonElement Input, string Queue,
    string[] After, InputBinding[] Bindings, FlowCondition? Condition, int MaxAttempts);
public enum FlowFailurePolicy { Stop, Suspend }
public sealed record FlowDefinition(string Key, int Version, FlowStep[] Steps, FlowFailurePolicy FailurePolicy = FlowFailurePolicy.Stop);
public sealed record WorkflowRun(Guid Id, string FlowKey, int Version, string? CorrelationId, string Status, string? Error);

/// <summary>Creates a data-only acyclic workflow. Step IDs are stable within a definition.</summary>
public sealed class FlowBuilder
{
    private readonly string key;
    private readonly int version;
    private readonly List<FlowStep> steps = [];
    private FlowFailurePolicy failurePolicy = FlowFailurePolicy.Suspend;

    /// <summary>Suspend retains dependent work for recovery; Stop permanently skips remaining work.</summary>
    public FlowBuilder OnFailure(FlowFailurePolicy policy) { failurePolicy = policy; return this; }

    public FlowBuilder(string key, int version)
    {
        _ = new JobContractId(key, version);
        this.key = key;
        this.version = version;
    }

    public FlowBuilder Step<TJob>(string id, object? input = null, string queue = "default", int maxAttempts = 3)
        => Step(id, JobContractId.For<TJob>(), input, queue, maxAttempts);

    /// <summary>Adds a processor invocation without a dependency on its implementation assembly.</summary>
    public FlowBuilder Step(string id, JobContractId processor, object? input = null, string queue = "default", int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (string.IsNullOrWhiteSpace(id) || steps.Any(x => x.Id == id))
            throw new ArgumentException("Step IDs must be non-empty and unique.", nameof(id));
        if (string.IsNullOrWhiteSpace(queue) || maxAttempts < 1) throw new ArgumentException("Invalid queue or retry policy.");
        steps.Add(new(id, processor, JsonSerializer.SerializeToElement(input ?? new { }, JobJson.Options),
            queue, [], [], null, maxAttempts));
        return this;
    }

    public FlowBuilder After(params string[] predecessors) => Change(step => step with { After = predecessors.Distinct().ToArray() });

    public FlowBuilder BindInput(string targetProperty, string sourceProperty, string? sourceStep = null) =>
        Change(step => step with { Bindings = [.. step.Bindings, new(targetProperty, sourceStep, sourceProperty)] });

    public FlowBuilder WhenOutput<T>(string sourceStep, string property, T equals) =>
        Change(step => step with { Condition = new(sourceStep, property, JsonSerializer.SerializeToElement(equals, JobJson.Options)) });

    public FlowDefinition Build()
    {
        var definition = new FlowDefinition(key, version, steps.ToArray(), failurePolicy);
        FlowValidator.Validate(definition);
        return definition;
    }

    private FlowBuilder Change(Func<FlowStep, FlowStep> change)
    {
        if (steps.Count == 0) throw new InvalidOperationException("Add a step first.");
        steps[^1] = change(steps[^1]);
        return this;
    }
}

public sealed class WorkflowClient(IJobStore store)
{
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);
        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), correlationId, cancellationToken);
    }
}
