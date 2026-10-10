using System.Text.Json;

namespace WorkThrough;

public static class FlowValidator
{
    public const int MaximumSteps = 500;
    public const int MaximumDisplayNameLength = 200;

    public static void Validate(FlowDefinition definition)
    {
        ValidateDraftShape(definition);
        if (definition.Steps.Length == 0)
        {
            throw new ArgumentException("A flow requires at least one step.");
        }

        var signals = definition.Steps.Where(s => s.Signal is not null).Select(s => s.Signal!.Name).ToArray();
        if (signals.Distinct(StringComparer.Ordinal).Count() != signals.Length)
        {
            throw new ArgumentException("Signal names must be unique within a flow revision.");
        }

        var steps = definition.Steps.ToDictionary(x => x.Id);
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();
        foreach (var step in definition.Steps)
        {
            Visit(step.Id);
        }

        foreach (var step in definition.Steps)
        {
            if (step.Signal is { } signal)
            {
                _ = new JobContractId(signal.Name, 1);
                if (step.Contract != SignalWait.Processor || !JobLimits.IsSupportedDelay(signal.Timeout))
                {
                    throw new ArgumentException("A signal wait requires a timeout of one millisecond to 365 days and the built-in signal address.");
                }
            }
            else if (step.Contract.Key == SignalWait.Processor.Key)
            {
                throw new ArgumentException("Use WaitForSignal to declare a durable wait.");
            }

            if (string.IsNullOrWhiteSpace(step.Id) || string.IsNullOrWhiteSpace(step.Queue) || step.MaxAttempts < 1)
            {
                throw new ArgumentException("Steps require an ID, queue, and positive retry count.");
            }

            if (step.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                throw new ArgumentException($"Input for {step.Id} must not be null.");
            }

            if (step.Bindings.Length > 0 && step.Input.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"Input bindings for {step.Id} require a JSON object.");
            }

            var ancestors = new HashSet<string>();
            Collect(step.Id);
            foreach (var reference in step.Bindings.Select(x => x.SourceStep).Append(step.Condition?.SourceStep).OfType<string>())
            {
                if (!ancestors.Contains(reference))
                {
                    throw new ArgumentException($"{step.Id} reads {reference}, which is not a dependency.");
                }
            }

            if (step.Bindings.Select(x => x.TargetProperty).Distinct().Count() != step.Bindings.Length)
            {
                throw new ArgumentException($"Duplicate input bindings in {step.Id}.");
            }

            void Collect(string id)
            {
                foreach (var predecessor in steps[id].After)
                {
                    if (ancestors.Add(predecessor))
                    {
                        Collect(predecessor);
                    }
                }
            }
        }

        void Visit(string id)
        {
            if (done.Contains(id))
            {
                return;
            }

            if (!steps.TryGetValue(id, out var step))
            {
                throw new ArgumentException($"Unknown dependency {id}.");
            }

            if (!visiting.Add(id))
            {
                throw new ArgumentException("Workflow contains a cycle.");
            }

            foreach (var predecessor in step.After)
            {
                Visit(predecessor);
            }

            visiting.Remove(id);
            done.Add(id);
        }
    }

    public static void ValidateDraftShape(FlowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = new JobContractId(definition.Key, definition.Version);
        if (!Enum.IsDefined(definition.FailurePolicy))
        {
            throw new ArgumentException("Unknown flow failure policy.");
        }

        if (definition.Steps is null || definition.Steps.Length > MaximumSteps)
        {
            throw new ArgumentException($"A definition requires a steps array of at most {MaximumSteps} nodes.");
        }

        foreach (var step in definition.Steps)
        {
            if (step?.DisplayName is { } label && (string.IsNullOrWhiteSpace(label) || label.Length > MaximumDisplayNameLength))
            {
                throw new ArgumentException($"Step display names must contain 1–{MaximumDisplayNameLength} characters.");
            }

            if (step is null || step.Contract is null || step.After is null || step.Bindings is null
                || step.After.Any(string.IsNullOrWhiteSpace) || step.Bindings.Any(b => b is null || string.IsNullOrWhiteSpace(b.TargetProperty) || string.IsNullOrWhiteSpace(b.SourceProperty)))
            {
                throw new ArgumentException("Steps require a processor, dependency array, and valid binding array.");
            }
        }
    }
}
