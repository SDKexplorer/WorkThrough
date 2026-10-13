using System.Linq.Expressions;
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

    /// <summary>
    /// Adds a step that runs <paramref name="processor"/> once per element of the array <paramref name="sourceProperty"/> in
    /// <paramref name="sourceStep"/>'s output. Each item step receives its element in <paramref name="itemProperty"/> of
    /// <paramref name="input"/>. Steps after this one wait for every item; see <see cref="FlowFanOut"/>.
    /// </summary>
    public FlowBuilder ForEach(string id, JobContractId processor, string sourceStep, string sourceProperty, string itemProperty, object? input = null,
        string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts)
    {
        Step(id, processor, input, queue, maxAttempts);

        return Change(step => step with { After = [.. step.After.Append(sourceStep).Distinct()], ForEach = new(sourceStep, sourceProperty, itemProperty) });
    }

    /// <summary>Typed form of <see cref="ForEach(string, JobContractId, string, string, string, object?, string, int)"/>, resolved to JSON names now.</summary>
    /// <example><c>.ForEach("ship", Shipping.Ship.Identity, "pick", (PickList list) => list.Parcels, (ShipParcel ship) => ship.Parcel)</c></example>
    public FlowBuilder ForEach<TSource, TItem, TInput>(string id, JobContractId processor, string sourceStep,
        Expression<Func<TSource, IEnumerable<TItem>>> items, Expression<Func<TInput, TItem>> item, object? input = null,
        string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts) =>
        ForEach(id, processor, sourceStep, JsonField.Of(items), JsonField.Of(item), input, queue, maxAttempts);

    /// <summary>
    /// Adds a step that runs the published flow <paramref name="child"/> with <paramref name="input"/>, bound like any step input,
    /// and waits up to <paramref name="timeout"/> (default 365 days) for it; see <see cref="FlowChild"/>.
    /// </summary>
    public FlowBuilder Child(string id, FlowDefinition child, object? input = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        Step(id, FlowChild.Processor, input);

        return Change(step => step with { Child = new(child.Key, child.Version, timeout ?? FlowChild.MaximumTimeout) { Definition = child } });
    }

    /// <summary>Runs the published flow <paramref name="child"/> once per element, each child receiving its element in <paramref name="itemProperty"/>.</summary>
    public FlowBuilder ForEach(string id, FlowDefinition child, string sourceStep, string sourceProperty, string itemProperty, object? input = null,
        TimeSpan? timeout = null)
    {
        ForEach(id, FlowChild.Processor, sourceStep, sourceProperty, itemProperty, input);

        return Change(step => step with { Child = new(child.Key, child.Version, timeout ?? FlowChild.MaximumTimeout) { Definition = child } });
    }

    /// <summary>
    /// Adds a step that runs the published flow <paramref name="child"/> repeatedly until property <paramref name="untilProperty"/> of
    /// its final step <paramref name="untilStep"/>'s output equals <paramref name="equals"/>, at most <paramref name="maximumIterations"/>
    /// times. Each run receives <paramref name="input"/> plus <c>iteration</c> and <c>previous</c>; see <see cref="FlowLoop"/>.
    /// </summary>
    public FlowBuilder Repeat<T>(string id, FlowDefinition child, string untilStep, string untilProperty, T equals, int maximumIterations,
        object? input = null, TimeSpan? timeout = null)
    {
        Child(id, child, input, timeout);

        return Change(step => step with
        {
            Loop = new(untilStep, untilProperty, JsonSerializer.SerializeToElement(equals, JobJson.Options), maximumIterations)
        });
    }

    /// <summary>
    /// Undoes the last step with <paramref name="processor"/> if the flow fails under <see cref="FlowFailurePolicy.Compensate"/>.
    /// The job receives the step's input and output as <see cref="CompensationInput{TInput, TOutput}"/>.
    /// </summary>
    public FlowBuilder CompensateWith(JobContractId processor, string queue = JobDefaults.Queue, int maxAttempts = JobDefaults.MaxAttempts) =>
        Change(step => step with { Compensation = new(processor, queue, maxAttempts) });

    /// <summary>Typed form of <see cref="CompensateWith(JobContractId, string, int)"/>.</summary>
    public FlowBuilder CompensateWith<TInput, TOutput>(JobDefinition<CompensationInput<TInput, TOutput>> definition, string queue = JobDefaults.Queue,
        int maxAttempts = JobDefaults.MaxAttempts) => CompensateWith(definition.Identity, queue, maxAttempts);

    /// <summary>Persists a signal wait without invoking a processor or holding a worker lease.</summary>
    public FlowBuilder WaitForSignal<TInput>(string id, SignalDefinition<TInput> signal, TimeSpan timeout)
    {
        Step(id, SignalWait.Processor);

        return Change(step => step with { Signal = new(signal.Name, timeout) });
    }

    /// <summary>
    /// Runs the last step only when <paramref name="stepId"/> fails, is rejected, expires or is cancelled, and handles that failure: the
    /// flow continues instead of applying its failure policy. Steps after the failed one are skipped; steps after this one may join them.
    /// </summary>
    public FlowBuilder OnFailureOf(string stepId) => Change(step => step with { After = [stepId], OnFailureOf = stepId });

    public FlowBuilder After(params string[] predecessors) => Change(step => step with { After = predecessors.Distinct().ToArray() });

    /// <summary>Labels the last step for operators without changing its permanent step ID.</summary>
    public FlowBuilder Named(string displayName) => Change(step => step with { DisplayName = displayName });

    public FlowBuilder BindInput(string targetProperty, string sourceProperty, string? sourceStep = null) =>
        Change(step => step with { Bindings = [.. step.Bindings, new(targetProperty, sourceStep, sourceProperty)] });

    public FlowBuilder WhenOutput<T>(string sourceStep, string property, T equals) =>
        Change(step => step with { Condition = new(sourceStep, property, JsonSerializer.SerializeToElement(equals, JobJson.Options)) });

    /// <summary>
    /// Sets a property of the last step's input from the flow input, or from <paramref name="sourceStep"/>'s output. Both sides are
    /// direct DTO properties, resolved to their JSON names now; only the names are saved.
    /// </summary>
    /// <example><c>.BindInput((ShipOrder ship) => ship.OrderId, (OrderPlaced order) => order.OrderId)</c></example>
    public FlowBuilder BindInput<TInput, TSource, TValue>(Expression<Func<TInput, TValue>> target, Expression<Func<TSource, TValue>> source,
        string? sourceStep = null) =>
        BindInput(JsonField.Of(target), JsonField.Of(source), sourceStep);

    /// <summary>Runs the last step only when <paramref name="sourceStep"/>'s output property equals <paramref name="equals"/>.</summary>
    public FlowBuilder WhenOutput<TOutput, TValue>(string sourceStep, Expression<Func<TOutput, TValue>> property, TValue equals) =>
        WhenOutput(sourceStep, JsonField.Of(property), equals);

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
