namespace WorkThrough;

/// <summary>Local process capacity. Shared limits are stored by the persistence provider.</summary>
public sealed class JobWorkerOptions
{
    public string WorkerId { get; set; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    public int MaxConcurrentJobs { get; set; } = 8;
    public Dictionary<string, int> PerJobLimits { get; set; } = [];
    public string[] Queues { get; set; } = [JobDefaults.Queue];
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Requests cooperative cancellation; non-cooperative code cannot be forcibly stopped.</summary>
    public TimeSpan? ExecutionTimeout { get; set; }
    public TimeSpan? ProgressTimeout { get; set; }
    /// <summary>Redact exception messages and traces here before they reach durable storage.</summary>
    public Func<Exception, JobFailure> DescribeFailure { get; set; } = JobFailure.FromException;
}
