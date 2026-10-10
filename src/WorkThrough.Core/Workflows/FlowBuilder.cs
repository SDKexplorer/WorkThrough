using System.Text.Json;

namespace WorkThrough;

/// <summary>Creates a data-only acyclic workflow. Step IDs are stable within a definition.</summary>
public sealed class FlowBuilder
{
    private readonly string key;
    private readonly int version;
    private readonly List<FlowStep> steps = [];
    private FlowFailurePolicy failurePolicy = FlowFailurePolicy.Suspend;

    public FlowBuilder(string key, int version)
    {
        _ = new JobContractId(key, version);
        this.key = key;
        this.version = version;
    }

    /// <summary>Suspend retains dependent work for recovery; Stop permanently skips remaining work.</summary>
    public FlowBuilder OnFailure(FlowFailurePolicy policy)
    {
        failurePolicy = policy;

        return this;
    }

    public FlowBuilder Step<TJob>(string id, object? input = null, string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts)
        => Step(id, JobContractId.For<TJob>(), input, queue, maxAttempts);

    /// <summary>Adds an invocation using a shared typed definition.</summary>
    public FlowBuilder Step<TInput>(string id, JobDefinition<TInput> definition, TInput input, string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts)
        => Step(id, definition.Identity, input, queue, maxAttempts);

    /// <summary>Adds a processor invocation without a dependency on its implementation assembly.</summary>
    public FlowBuilder Step(string id, JobContractId processor, object? input = null, string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (string.IsNullOrWhiteSpace(id) || steps.Any(x => x.Id == id))
        {
            throw new ArgumentException("Step IDs must be non-empty and unique.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(queue) || maxAttempts < 1)
        {
            throw new ArgumentException("Invalid queue or retry policy.");
        }

        var payload = JsonSerializer.SerializeToElement(input ?? new { }, JobJson.Options);
        steps.Add(new(id, processor, payload, queue, [], [], null, maxAttempts));

        return this;
    }

    /// <summary>Persists a signal wait without invoking a processor or holding a worker lease.</summary>
    public FlowBuilder WaitForSignal<TInput>(string id, SignalDefinition<TInput> signal, TimeSpan timeout)
    {
        Step(id, SignalWait.Processor);

        return Change(step => step with { Signal = new(signal.Name, timeout) });
    }

    public FlowBuilder After(params string[] predecessors) => Change(step => step with { After = predecessors.Distinct().ToArray() });

    /// <summary>Labels the last step for operators without changing its permanent step ID.</summary>
    public FlowBuilder Named(string displayName) => Change(step => step with { DisplayName = displayName });

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
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("Add a step first.");
        }

        steps[^1] = change(steps[^1]);

        return this;
    }
}
