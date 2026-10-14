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
                if (definition.FailurePolicy == FlowFailurePolicy.Suspend && FirstUnsuccessful(definition, current) is { } incident)
                {
                    return Plan(changes, FlowStatus.Incident, RecoveryMessage(incident), added);
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
                if (step.ForEach is not null && job.Status is JobStatus.Queued or JobStatus.AwaitingSignal)
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

        if (FirstUnsuccessful(definition, current) is not { } failure)
        {
            return Plan(changes, current.Values.All(x => x.Status.IsTerminal()) ? FlowStatus.Succeeded : FlowStatus.Running, null, added);
        }

        if (definition.FailurePolicy == FlowFailurePolicy.Suspend)
        {
            return Plan(changes, FlowStatus.Incident, RecoveryMessage(failure), added);
        }

        // Item steps created by this plan are not stored yet; a failing flow simply never creates them.
        var created = added.Select(item => item.Id).ToHashSet();
        added.Clear();
        foreach (var job in current.Values.Where(x => x.Status is JobStatus.Waiting or JobStatus.Queued && !created.Contains(x.Id)
            && !FlowCompensation.IsUndoStep(x.StepId)).ToArray())
        {
            current[job.StepId!] = job with { Status = JobStatus.Skipped };
            changes.Add(current[job.StepId!]);
        }

        foreach (var id in created)
        {
            current.Remove(current.Single(entry => entry.Value.Id == id).Key);
        }

        var error = failure.Error ?? $"Step {failure.StepId} did not succeed.";

        return definition.FailurePolicy == FlowFailurePolicy.Compensate
            ? Compensate(definition, current, changes, added, error)
            : Plan(changes, FlowStatus.Failed, error, added);
    }

    /// <summary>
    /// Undoes completed steps that declare a compensation. A step is undone after every step that depended on it has finished and been
    /// undone. A failed compensation raises an incident; the flow fails once nothing remains to run.
    /// </summary>
    private static FlowPlan Compensate(FlowDefinition definition, Dictionary<string, JobRecord> current, List<JobRecord> changes, List<JobRecord> added,
        string error)
    {
        foreach (var step in definition.Steps.Where(step => step.Compensation is not null))
        {
            var undoId = FlowCompensation.UndoStepId(step.Id);
            if (current[step.Id] is { Status: JobStatus.Succeeded } done && !current.ContainsKey(undoId))
            {
                var compensation = step.Compensation!;
                var undo = done with
                {
                    Id = Guid.NewGuid(),
                    StepId = undoId,
                    Contract = compensation.Contract,
                    Queue = compensation.Queue,
                    MaxAttempts = compensation.MaxAttempts,
                    Status = JobStatus.Waiting,
                    Attempt = 0,
                    Output = null,
                    Error = null,
                    Payload = JobJson.ToElement(new JsonObject
                    {
                        ["input"] = JsonNode.Parse(done.Payload.GetRawText()),
                        ["output"] = done.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null
                    })
                };
                current[undoId] = undo;
                added.Add(undo);
            }
        }

        var progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (var step in definition.Steps)
            {
                if (!current.TryGetValue(FlowCompensation.UndoStepId(step.Id), out var undo) || undo.Status != JobStatus.Waiting)
                {
                    continue;
                }

                // Later work is undone first: every descendant must have finished, and its own compensation too.
                var settled = definition.Descendants(step.Id).All(id => current[id].Status.IsTerminal()
                    && (!current.TryGetValue(FlowCompensation.UndoStepId(id), out var later) || later.Status.IsTerminal()));
                if (!settled)
                {
                    continue;
                }

                var queued = undo with { Status = JobStatus.Queued };
                current[queued.StepId!] = queued;
                var pending = added.FindIndex(item => item.Id == queued.Id);
                if (pending >= 0)
                {
                    added[pending] = queued;
                }
                else
                {
                    changes.Add(queued);
                }

                progressed = true;
            }
        }

        if (current.Values.FirstOrDefault(job => FlowCompensation.IsUndoStep(job.StepId) && job.Status.IsUnsuccessful()) is { } failedUndo)
        {
            return Plan(changes, FlowStatus.Incident, $"Compensation {failedUndo.StepId} did not succeed: {failedUndo.Error}", added);
        }

        return Plan(changes, current.Values.All(job => job.Status.IsTerminal()) ? FlowStatus.Failed : FlowStatus.Running, error, added);
    }

    /// <summary>A plan whose changes keep only the last change to each job, since providers apply each against the status first read.</summary>
    private static FlowPlan Plan(List<JobRecord> changes, FlowStatus status, string? error, List<JobRecord> added) =>
        new([.. changes.GroupBy(job => job.Id).Select(group => group.Last())], status, error) { Added = added };

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
                Status = step.Child is null ? JobStatus.Queued : JobStatus.AwaitingSignal,
                Payload = JobJson.ToElement(payload),
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
        Output = JobJson.ToElement(new JsonObject
        {
            [FlowFanOut.ItemsProperty] = new JsonArray([.. items.Select(item => item.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null)])
        })
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

            var payload = BindPayload(step, input, current);

            return job with
            {
                Status = step.Signal is null && step.Child is null ? JobStatus.Queued : JobStatus.AwaitingSignal,
                Payload = step.OnFailureOf is { } failed ? WithFailure(payload, current[failed]) : payload
            };
        }
        catch (InvalidOperationException exception)
        {
            return job with { Status = JobStatus.Failed, Error = exception.Message };
        }
    }

    private static bool IsEnabled(FlowStep step, JobRecord[] predecessors, Dictionary<string, JobRecord> current)
    {
        if (step.OnFailureOf is { } failed)
        {
            return current[failed].Status.IsUnsuccessful();
        }

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

        return JobJson.ToElement(payload);
    }

    /// <summary>A failure branch's input: its bound input plus the step that failed and its error.</summary>
    private static JsonElement WithFailure(JsonElement payload, JobRecord failed)
    {
        var input = JsonNode.Parse(payload.GetRawText())!;
        input[FlowFailure.Property] = JobJson.ToNode(new FlowFailure(failed.StepId!, failed.Error));

        return JobJson.ToElement(input);
    }

    /// <summary>
    /// The first failed step of the flow's own work that no failure branch handles; compensations are judged separately.
    /// </summary>
    private static JobRecord? FirstUnsuccessful(FlowDefinition definition, Dictionary<string, JobRecord> jobs) =>
        jobs.Values.FirstOrDefault(x => x.Status.IsUnsuccessful() && !FlowCompensation.IsUndoStep(x.StepId)
            && !definition.IsHandled(FlowFanOut.BaseStepId(x.StepId!)));

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
