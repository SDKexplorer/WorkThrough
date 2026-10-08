using System.Text.Json;

namespace WorkThrough;

public sealed record EnqueueOptions(
    string Queue = "default", string? CorrelationId = null, DateTimeOffset? NotBefore = null,
    int MaxAttempts = 3, string? IdempotencyKey = null);

/// <summary>Accepts work durably without requiring any handler registrations.</summary>
public sealed class JobClient(IJobStore store)
{
    public Task<Guid> EnqueueAsync<TJob>(TJob job, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
        => EnqueueAsync(JobContractId.For<TJob>(), job, options, cancellationToken);

    /// <summary>Enqueues typed or raw-string input without referencing the processor assembly.</summary>
    public Task<Guid> EnqueueAsync<TInput>(JobContractId processor, TInput job, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(job);
        options ??= new();
        if (options.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be positive.");
        if (string.IsNullOrWhiteSpace(options.Queue)) throw new ArgumentException("Queue must be non-empty.", nameof(options));
        if (options.IdempotencyKey is { Length: 0 }) throw new ArgumentException("IdempotencyKey must be non-empty when supplied.", nameof(options));
        var now = DateTimeOffset.UtcNow;
        // Immediate work has no not-before boundary. Use the current storage-resolution instant;
        // explicit NotBefore values keep their precision and are rounded up by the provider.
        var available = options.NotBefore ?? DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        return store.EnqueueAsync(new(Guid.NewGuid(), processor,
            JsonSerializer.SerializeToElement(job, JobJson.Options), options.Queue, options.CorrelationId,
            JobStatus.Queued, 0, options.MaxAttempts, now, available,
            options.IdempotencyKey), cancellationToken);
    }
}
