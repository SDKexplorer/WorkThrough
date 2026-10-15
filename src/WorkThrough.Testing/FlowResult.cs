using System.Text.Json;

namespace WorkThrough.Testing;

/// <summary>A flow run and its steps, with assertions that report every step when they fail.</summary>
public sealed class FlowResult(WorkflowRun run, IReadOnlyList<JobRecord> jobs)
{
    public WorkflowRun Run { get; } = run;
    public IReadOnlyList<JobRecord> Jobs { get; } = jobs;

    /// <summary>The step's job. Fan-out items are named <c>step[index]</c>.</summary>
    public StepResult Step(string stepId) => new(Jobs.SingleOrDefault(job => job.StepId == stepId)
        ?? throw new WorkThroughAssertionException($"Flow {Run.FlowKey} has no step {stepId}. {Describe()}"));

    public FlowResult AssertSucceeded() => AssertStatus(FlowStatus.Succeeded);

    public FlowResult AssertStatus(FlowStatus expected) => Run.Status == expected ? this
        : throw new WorkThroughAssertionException($"Expected flow {Run.FlowKey} to be {expected}. {Describe()}");

    /// <summary>The run's status and error, and every step's status and error.</summary>
    public string Describe() =>
        $"Flow {Run.FlowKey} v{Run.Version} is {Run.Status}{(Run.Error is null ? "" : $": {Run.Error}")}. Steps: " +
        string.Join("; ", Jobs.OrderBy(job => job.StepId, StringComparer.Ordinal)
            .Select(job => $"{job.StepId} {job.Status}{(job.Error is null ? "" : $" ({job.Error})")}"));
}

/// <summary>One step's job, with its output read as a DTO.</summary>
public sealed class StepResult(JobRecord job)
{
    public JobRecord Job { get; } = job;
    public JobStatus Status => Job.Status;
    public string? Error => Job.Error;

    public T Output<T>() => Job.Output is { } output
        ? JobJson.Deserialize<T>(output)!
        : throw new WorkThroughAssertionException($"Step {Job.StepId} has no output; it is {Job.Status}.");

    public T Input<T>() => JobJson.Deserialize<T>(Job.Payload)!;
}

/// <summary>A test expectation about a run or step did not hold.</summary>
public sealed class WorkThroughAssertionException(string message) : Exception(message);
