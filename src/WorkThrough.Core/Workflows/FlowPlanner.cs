using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkThrough;

public sealed record FlowPlan(IReadOnlyList<JobRecord> Changes, string Status, string? Error);

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
                if (definition.FailurePolicy == FlowFailurePolicy.Suspend
                    && current.Values.FirstOrDefault(x => x.Status is JobStatus.Failed or JobStatus.Cancelled) is { } incident)
                    return new(changes, "Incident", incident.Error ?? $"Step {incident.StepId} requires recovery.");
                var job = current[step.Id];
                if (job.Status != JobStatus.Waiting) continue;
                var predecessors = step.After.Select(id => current[id]).ToArray();
                if (predecessors.Any(x => !Terminal(x.Status))) continue;
                try
                {
                    var enabled = predecessors.All(x => x.Status is JobStatus.Succeeded or JobStatus.Skipped)
                        && (predecessors.Length == 0 || predecessors.Any(x => x.Status == JobStatus.Succeeded));
                    if (enabled && step.Condition is { } condition)
                        enabled = JsonNode.DeepEquals(JsonNode.Parse(Read(current[condition.SourceStep].Output, condition.Property).GetRawText()),
                            JsonNode.Parse(condition.Expected.GetRawText()));
                    if (enabled)
                    {
                        var payload = JsonNode.Parse(step.Input.GetRawText());
                        if (step.Bindings.Length > 0 && payload is not JsonObject)
                            throw new InvalidOperationException("Property bindings require an object input.");
                        foreach (var binding in step.Bindings)
                        {
                            var source = binding.SourceStep is null ? input : current[binding.SourceStep].Output;
                            payload![binding.TargetProperty] = JsonNode.Parse(Read(source, binding.SourceProperty).GetRawText());
                        }
                        job = job with { Status = JobStatus.Queued, Payload = JsonSerializer.SerializeToElement(payload, JobJson.Options) };
                    }
                    else job = job with { Status = JobStatus.Skipped };
                }
                catch (InvalidOperationException exception)
                {
                    job = job with { Status = JobStatus.Failed, Error = exception.Message };
                }
                current[step.Id] = job;
                changes.Add(job);
                progressed = true;
            }
        }

        var failure = current.Values.FirstOrDefault(x => x.Status is JobStatus.Failed or JobStatus.Cancelled);
        if (failure is not null)
        {
            if (definition.FailurePolicy == FlowFailurePolicy.Suspend)
                return new(changes, "Incident", failure.Error ?? $"Step {failure.StepId} requires recovery.");
            foreach (var job in current.Values.Where(x => x.Status is JobStatus.Waiting or JobStatus.Queued).ToArray())
                changes.Add(job with { Status = JobStatus.Skipped });
            return new(changes, "Failed", failure.Error ?? $"Step {failure.StepId} did not succeed.");
        }
        return new(changes, current.Values.All(x => Terminal(x.Status)) ? "Succeeded" : "Running", null);
    }

    private static bool Terminal(JobStatus status) => status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Skipped or JobStatus.Cancelled;

    private static JsonElement Read(JsonElement? source, string property)
    {
        if (source is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(property, out var result))
            throw new InvalidOperationException($"Workflow input/output does not contain '{property}'.");
        return result;
    }
}
