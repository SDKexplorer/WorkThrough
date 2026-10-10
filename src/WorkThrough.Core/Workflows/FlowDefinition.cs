using System.Text.Json;

namespace WorkThrough;

public sealed record InputBinding(string TargetProperty, string? SourceStep, string SourceProperty);
public sealed record FlowCondition(string SourceStep, string Property, JsonElement Expected);
public sealed record FlowStep(
    string Id, JobContractId Contract, JsonElement Input, string Queue,
    string[] After, InputBinding[] Bindings, FlowCondition? Condition, int MaxAttempts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SignalWait? Signal = null)
{
    /// <summary>Optional saved display label. Dependencies and dispatch continue to use permanent identities.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }
}
public enum FlowFailurePolicy
{
    Stop, Suspend
}
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
    public FlowBuilder OnFailure(FlowFailurePolicy policy)
    {
        failurePolicy = policy;

        return this;
    }

    public FlowBuilder(string key, int version)
    {
        _ = new JobContractId(key, version);
        this.key = key;
        this.version = version;
    }

    public FlowBuilder Step<TJob>(string id, object? input = null, string queue = "default", int maxAttempts = 3)
        => Step(id, JobContractId.For<TJob>(), input, queue, maxAttempts);

    /// <summary>Adds an invocation using a shared typed definition.</summary>
    public FlowBuilder Step<TInput>(string id, JobDefinition<TInput> definition, TInput input, string queue = "default", int maxAttempts = 3)
        => Step(id, definition.Identity, input, queue, maxAttempts);

    /// <summary>Adds a processor invocation without a dependency on its implementation assembly.</summary>
    public FlowBuilder Step(string id, JobContractId processor, object? input = null, string queue = "default", int maxAttempts = 3)
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

        steps.Add(new(id, processor, JsonSerializer.SerializeToElement(input ?? new
        {
        }, JobJson.Options),
            queue, [], [], null, maxAttempts));

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

public sealed class WorkflowClient(IJobStore store)
{
    public Task<bool> SignalAsync<TInput>(Guid runId, SignalDefinition<TInput> signal, TInput input, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (store is not IWorkflowSignals signals)
        {
            throw new NotSupportedException("This provider does not support durable signals.");
        }

        return signals.SignalAsync(runId, signal.Name, JsonSerializer.SerializeToElement(input, JobJson.Options), idempotencyKey, cancellationToken);
    }

    /// <summary>Starts a new run. Correlation groups observations and does not deduplicate requests.</summary>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);

        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), correlationId, cancellationToken);
    }

    /// <summary>Returns the same logical run when an identical request is repeated with the same store-scoped key.</summary>
    /// <remarks>Reusing a key with different serialized definition, input, or correlation fails. Effects remain at least once.</remarks>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);
        ValidateIdempotencyKey(idempotencyKey);

        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), correlationId, idempotencyKey, cancellationToken);
    }

    /// <summary>Validates the store-scoped flow-start request key.</summary>
    public static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException("Flow idempotency keys must contain 1–256 characters and cannot be whitespace.", nameof(idempotencyKey));
        }
    }
}
