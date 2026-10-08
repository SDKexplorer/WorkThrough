namespace WorkThrough;

/// <summary>Declared capabilities of one worker profile in the proposed complete deployment.</summary>
public sealed record WorkerCapability(string Name, IReadOnlyList<JobContractId> Processors, IReadOnlyList<string> Queues);

/// <summary>Outstanding stored work grouped by permanent processor identity and queue; contains no payloads.</summary>
public sealed record DeploymentRequirement(JobContractId Processor, string Queue, long OutstandingJobs, long ReadyJobs,
    DateTimeOffset? OldestReadyAt, int EnabledSchedules);

public sealed record DeploymentRequirements(DateTimeOffset ObservedAt, IReadOnlyList<DeploymentRequirement> Items, bool IsTruncated);
public enum DeploymentCoverage
{
    Covered, MissingProcessor, MissingQueue
}
public sealed record DeploymentCheck(DeploymentRequirement Requirement, DeploymentCoverage Coverage, IReadOnlyList<string> Workers);
public sealed record DeploymentReport(DateTimeOffset ObservedAt, IReadOnlyList<DeploymentCheck> Checks, bool IsTruncated)
{
    /// <summary>Identity and queue coverage only; this does not prove semantic compatibility or sufficient capacity.</summary>
    public bool AllRequirementsCovered => !IsTruncated && Checks.All(x => x.Coverage == DeploymentCoverage.Covered);
}

/// <summary>Reads existing requirements without claiming jobs, loading processors, or changing definitions.</summary>
public interface IDeploymentQueries
{
    Task<DeploymentRequirements> DeploymentRequirementsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Compares saved work with an explicit worker fleet before replacing a deployment.</summary>
public sealed class DeploymentPreflight(IDeploymentQueries queries)
{
    public async Task<DeploymentReport> CheckAsync(IReadOnlyList<WorkerCapability> proposedWorkers, CancellationToken cancellationToken = default)
    {
        Validate(proposedWorkers);

        return Compare(await queries.DeploymentRequirementsAsync(cancellationToken), proposedWorkers);
    }

    /// <summary>Each processor/queue pair must be supported by the same worker profile.</summary>
    public static DeploymentReport Compare(DeploymentRequirements requirements, IReadOnlyList<WorkerCapability> proposedWorkers)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        Validate(proposedWorkers);
        var checks = requirements.Items.Select(requirement =>
        {
            var processors = proposedWorkers.Where(w => w.Processors.Contains(requirement.Processor)).ToArray();
            var matches = processors.Where(w => w.Queues.Contains(requirement.Queue, StringComparer.Ordinal)).Select(w => w.Name).ToArray();

            return new DeploymentCheck(requirement, matches.Length > 0 ? DeploymentCoverage.Covered
                : processors.Length == 0 ? DeploymentCoverage.MissingProcessor : DeploymentCoverage.MissingQueue, matches);
        }).ToArray();

        return new(requirements.ObservedAt, checks, requirements.IsTruncated);
    }

    private static void Validate(IReadOnlyList<WorkerCapability> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        if (workers.Count > 500)
        {
            throw new ArgumentException("A deployment report supports at most 500 worker profiles.", nameof(workers));
        }

        foreach (var worker in workers)
        {
            if (worker is null || string.IsNullOrWhiteSpace(worker.Name) || worker.Name.Length > 256
                || worker.Processors is null || worker.Queues is null || worker.Processors.Count > 1000 || worker.Queues.Count > 1000
                || worker.Processors.Any(p => p is null) || worker.Queues.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Worker profiles require a name, processor identities, and queues within the documented bounds.", nameof(workers));
            }
        }

        if (workers.Select(w => w.Name).Distinct(StringComparer.Ordinal).Count() != workers.Count)
        {
            throw new ArgumentException("Worker profile names must be unique.", nameof(workers));
        }
    }
}
