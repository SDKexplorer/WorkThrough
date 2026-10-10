using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkThrough;

public sealed record FlowPlan(IReadOnlyList<JobRecord> Changes, FlowStatus Status, string? Error);

/// <summary>Pure transition logic. Providers commit its changes with predecessor completion.</summary>
public static class FlowPlanner
{
    public static FlowPlan Advance(FlowDefinition definition, JsonElement input, IReadOnlyList<JobRecord> jobs)
    {
        var current = jobs.ToDictionary(x => x.StepId!);
        var changes = new List<JobRecord>();
        var progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (var step in definition.Steps)
            {
                if (definition.FailurePolicy == FlowFailurePolicy.Suspend && FirstUnsuccessful(current) is { } incident)
                {
                    return new(changes, FlowStatus.Incident, RecoveryMessage(incident));
                }

                var job = current[step.Id];
                var predecessors = step.After.Select(id => current[id]).ToArray();
                if (job.Status != JobStatus.Waiting || predecessors.Any(x => !x.Status.IsTerminal()))
                {
                    continue;
                }

                job = Decide(step, job, predecessors, input, current);
                current[step.Id] = job;
                changes.Add(job);
                progressed = true;
            }
        }

        if (FirstUnsuccessful(current) is not { } failure)
        {
            return new(changes, current.Values.All(x => x.Status.IsTerminal()) ? FlowStatus.Succeeded : FlowStatus.Running, null);
        }

        if (definition.FailurePolicy == FlowFailurePolicy.Suspend)
        {
            return new(changes, FlowStatus.Incident, RecoveryMessage(failure));
        }

        foreach (var job in current.Values.Where(x => x.Status is JobStatus.Waiting or JobStatus.Queued).ToArray())
        {
            changes.Add(job with { Status = JobStatus.Skipped });
        }

        return new(changes, FlowStatus.Failed, failure.Error ?? $"Step {failure.StepId} did not succeed.");
    }

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
