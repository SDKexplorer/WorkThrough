using System.Text.Json;

namespace WorkThrough;

public sealed record EnqueueOptions(
    string Queue = JobDefaults.Queue, string? CorrelationId = null, DateTimeOffset? NotBefore = null,
    int MaxAttempts = JobDefaults.MaxAttempts, string? IdempotencyKey = null, DateTimeOffset? ExpiresAt = null,
    string? PartitionKey = null);

/// <summary>Accepts work durably without requiring any handler registrations.</summary>
public sealed class JobClient(IJobStore store)
{
    /// <summary>Enqueues the input type declared by a shared job definition.</summary>
    public Task<Guid> EnqueueAsync<TInput>(JobDefinition<TInput> definition, TInput input, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return EnqueueAsync(definition.Identity, input, options, cancellationToken);
    }

    public Task<Guid> EnqueueAsync<TJob>(TJob job, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
        => EnqueueAsync(JobContractId.For<TJob>(), job, options, cancellationToken);

    /// <summary>Enqueues typed or raw-string input without referencing the processor assembly.</summary>
    public Task<Guid> EnqueueAsync<TInput>(JobContractId processor, TInput job, EnqueueOptions? options = null, CancellationToken cancellationToken = default) =>
        store.EnqueueAsync(CreateRecord(processor, job, options), cancellationToken);

    /// <summary>Builds the validated envelope used by direct and transactional acceptance.</summary>
    public static JobRecord CreateRecord<TInput>(JobContractId processor, TInput job, EnqueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(job);
        if (processor.Key == SignalWait.Processor.Key)
        {
            throw new ArgumentException("Use a flow signal wait instead of enqueueing the reserved signal address.", nameof(processor));
        }

        options ??= new();
        if (options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.Queue))
        {
            throw new ArgumentException("Queue must be non-empty.", nameof(options));
        }

        PartitionKeys.Validate(options.PartitionKey, nameof(options));
        if (options.IdempotencyKey is { Length: 0 })
        {
            throw new ArgumentException("IdempotencyKey must be non-empty when supplied.", nameof(options));
        }

        var now = DateTimeOffset.UtcNow;
        // Immediate work has no not-before boundary. Storage replaces these provisional times
        // on acceptance; explicit NotBefore values retain their requested precision.
        var available = options.NotBefore ?? DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var payload = JsonSerializer.SerializeToElement(job, JobJson.Options);
        if (JobLimits.ExceedsInlineSize(payload))
        {
            throw new ArgumentException("Inline input exceeds 1 MiB. Store large documents externally and enqueue a reference.", nameof(job));
        }

        if (options.NotBefore is { } due && options.ExpiresAt <= due)
        {
            throw new ArgumentException("Expiration must follow NotBefore.", nameof(options));
        }

        return new(Guid.NewGuid(), processor,
            payload, options.Queue, options.CorrelationId,
            JobStatus.Queued, 0, options.MaxAttempts, now, available,
            options.IdempotencyKey, ExpiresAt: options.ExpiresAt)
        {
            UseStorageTime = options.NotBefore is null,
            PartitionKey = options.PartitionKey
        };
    }
}
