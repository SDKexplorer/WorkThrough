using System.Text.Json;

namespace WorkThrough;

/// <summary>Exponential backoff capped at one minute, with jitter. Invalid input and explicit rejection are not retried.</summary>
internal sealed class DefaultRetryPolicy : IJobRetryPolicy
{
    private const int MaximumBackoffSeconds = 60;
    private const int MaximumBackoffExponent = 6;
    private const int MaximumJitterMilliseconds = 250;

    public RetryDecision Decide(JobRecord job, Exception exception)
    {
        if (exception is JsonException or JobRejectedException)
        {
            return RetryDecision.Stop;
        }

        var backoff = Math.Min(MaximumBackoffSeconds, Math.Pow(2, Math.Min(job.Attempt, MaximumBackoffExponent)));

        return RetryDecision.After(TimeSpan.FromSeconds(backoff) + TimeSpan.FromMilliseconds(Random.Shared.Next(MaximumJitterMilliseconds)));
    }
}
