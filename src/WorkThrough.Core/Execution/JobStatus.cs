namespace WorkThrough;

/// <summary>Persisted by name. Renaming a member is a storage compatibility change.</summary>
public enum JobStatus
{
    Waiting, Queued, Running, Succeeded, Failed, Skipped, Cancelled, Rejected, Expired, AwaitingSignal
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Skipped
        or JobStatus.Cancelled or JobStatus.Rejected or JobStatus.Expired;

    /// <summary>Terminal without success. A suspending flow treats these steps as incidents.</summary>
    public static bool IsUnsuccessful(this JobStatus status) => status is JobStatus.Failed or JobStatus.Cancelled
        or JobStatus.Rejected or JobStatus.Expired;
}
