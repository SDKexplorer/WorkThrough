using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkThrough;

public sealed record FlowPlan(IReadOnlyList<JobRecord> Changes, FlowStatus Status, string? Error)
{
    /// <summary>Item steps that fan-out steps created; providers insert them with the changes.</summary>
    public IReadOnlyList<JobRecord> Added { get; init; } = [];
}

/// <summary>Pure transition logic. Providers commit its changes with predecessor completion.</summary>
public static class FlowPlanner
{
    public static FlowPlan Advance(FlowDefinition definition, JsonElement input, IReadOnlyList<JobRecord> jobs)
    {
        var current = jobs.ToDictionary(x => x.StepId!);
        var changes = new List<JobRecord>();
        var added = new List<JobRecord>();
        var progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (var step in definition.Steps)
            {
                if (definition.FailurePolicy == FlowFailurePolicy.Suspend && FirstUnsuccessful(current) is { } incident)
                {
                    return new(changes, FlowStatus.Incident, RecoveryMessage(incident)) { Added = added };
                }

                var job = current[step.Id];
                var predecessors = step.After.Select(id => current[id]).ToArray();
                if (job.Status != JobStatus.Waiting || predecessors.Any(x => !x.Status.IsTerminal()))
                {
                    continue;
                }

                // An expanded fan-out stays waiting until its items finish; it is never expanded twice.
                if (step.ForEach is not null && ItemsOf(current, step.Id) is { Length: > 0 } items)
                {
                    if (items.All(item => item.Status is JobStatus.Succeeded or JobStatus.Skipped))
                    {
                        job = Gather(job, items);
                        current[step.Id] = job;
                        changes.Add(job);
                        progressed = true;
                    }

                    continue;
                }

                job = Decide(step, job, predecessors, input, current);
                if (step.ForEach is not null && job.Status == JobStatus.Queued)
                {
                    var expanded = Expand(step, job, current);
                    foreach (var item in expanded)
                    {
                        current[item.StepId!] = item;
                    }

                    added.AddRange(expanded);
                    job = current[step.Id];
                    if (expanded.Length > 0)
                    {
                        progressed = true;

                        continue;
                    }
                }

                current[step.Id] = job;
                changes.Add(job);
                progressed = true;
            }
        }

        if (FirstUnsuccessful(current) is not { } failure)
        {
            return new(changes, current.Values.All(x => x.Status.IsTerminal()) ? FlowStatus.Succeeded : FlowStatus.Running, null) { Added = added };
        }

        if (definition.FailurePolicy == FlowFailurePolicy.Suspend)
        {
            return new(changes, FlowStatus.Incident, RecoveryMessage(failure)) { Added = added };
        }

        // Item steps created by this plan are not stored yet; a failing flow simply never creates them.
        var created = added.Select(item => item.Id).ToHashSet();
        foreach (var job in current.Values.Where(x => x.Status is JobStatus.Waiting or JobStatus.Queued && !created.Contains(x.Id)).ToArray())
        {
            changes.Add(job with { Status = JobStatus.Skipped });
        }

        return new(changes, FlowStatus.Failed, failure.Error ?? $"Step {failure.StepId} did not succeed.");
    }

    /// <summary>
    /// Creates one queued item step per element of the fan-out's source array, each with the step's bound input plus its element.
    /// An empty array completes the step at once; an array over <see cref="FlowFanOut.MaximumItems"/> fails it.
    /// </summary>
    private static JobRecord[] Expand(FlowStep step, JobRecord group, Dictionary<string, JobRecord> current)
    {
        var forEach = step.ForEach!;
        var source = Read(current[forEach.SourceStep].Output, forEach.SourceProperty);
        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > FlowFanOut.MaximumItems)
        {
            current[step.Id] = group with
            {
                Status = JobStatus.Failed,
                Error = $"{forEach.SourceStep}.{forEach.SourceProperty} must be an array of at most {FlowFanOut.MaximumItems} items."
            };

            return [];
        }

        if (source.GetArrayLength() == 0)
        {
            current[step.Id] = Gather(group, []);

            return [];
        }

        return [.. source.EnumerateArray().Select((element, index) =>
        {
            var payload = JsonNode.Parse(group.Payload.GetRawText())!;
            payload[forEach.TargetProperty] = JsonNode.Parse(element.GetRawText());

            return group with
            {
                Id = Guid.NewGuid(),
                StepId = FlowFanOut.ItemStepId(step.Id, index),
                Status = JobStatus.Queued,
                Payload = JsonSerializer.SerializeToElement(payload, JobJson.Options),
                Attempt = 0,
                Output = null,
                Error = null
            };
        })];
    }

    /// <summary>Completes a fan-out step with its item outputs in item order.</summary>
    private static JobRecord Gather(JobRecord group, JobRecord[] items) => group with
    {
        Status = JobStatus.Succeeded,
        Output = JsonSerializer.SerializeToElement(new JsonObject
        {
            [FlowFanOut.ItemsProperty] = new JsonArray([.. items.Select(item => item.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null)])
        }, JobJson.Options)
    };

    /// <summary>The item steps of a fan-out step, in item order.</summary>
    private static JobRecord[] ItemsOf(Dictionary<string, JobRecord> current, string stepId) =>
    [
        .. current.Values.Where(job => job.StepId!.StartsWith(stepId + "[", StringComparison.Ordinal))
            .OrderBy(job => int.Parse(job.StepId![(stepId.Length + 1)..^1], System.Globalization.CultureInfo.InvariantCulture))
    ];

    private static JobRecord Decide(FlowStep step, JobRecord job, JobRecord[] predecessors, JsonElement input, Dictionary<string, JobRecord> current)
    {
        try
        {
            if (!IsEnabled(step, predecessors, current))
            {
                return job with { Status = JobStatus.Skipped };
            }

            return job with
            {
                Status = step.Signal is null ? JobStatus.Queued : JobStatus.AwaitingSignal,
                Payload = BindPayload(step, input, current)
            };
        }
        catch (InvalidOperationException exception)
        {
            return job with { Status = JobStatus.Failed, Error = exception.Message };
        }
    }

    private static bool IsEnabled(FlowStep step, JobRecord[] predecessors, Dictionary<string, JobRecord> current)
    {
        var enabled = predecessors.All(x => x.Status is JobStatus.Succeeded or JobStatus.Skipped)
            && (predecessors.Length == 0 || predecessors.Any(x => x.Status == JobStatus.Succeeded));
        if (!enabled || step.Condition is not { } condition)
        {
            return enabled;
        }

        var actual = Read(current[condition.SourceStep].Output, condition.Property);

        return JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(condition.Expected.GetRawText()));
    }

    private static JsonElement BindPayload(FlowStep step, JsonElement input, Dictionary<string, JobRecord> current)
    {
        var payload = JsonNode.Parse(step.Input.GetRawText());
        if (step.Bindings.Length > 0 && payload is not JsonObject)
        {
            throw new InvalidOperationException("Property bindings require an object input.");
        }

        foreach (var binding in step.Bindings)
        {
            var source = binding.SourceStep is null ? input : current[binding.SourceStep].Output;
            payload![binding.TargetProperty] = JsonNode.Parse(Read(source, binding.SourceProperty).GetRawText());
        }

        return JsonSerializer.SerializeToElement(payload, JobJson.Options);
    }

    private static JobRecord? FirstUnsuccessful(Dictionary<string, JobRecord> jobs) => jobs.Values.FirstOrDefault(x => x.Status.IsUnsuccessful());

    private static string RecoveryMessage(JobRecord step) => step.Error ?? $"Step {step.StepId} requires recovery.";

    private static JsonElement Read(JsonElement? source, string property)
    {
        if (source is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(property, out var result))
        {
            throw new InvalidOperationException($"Workflow input/output does not contain '{property}'.");
        }

        return result;
    }
}
