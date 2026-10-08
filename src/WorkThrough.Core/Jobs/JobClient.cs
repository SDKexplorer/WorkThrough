using System.Text.Json;

namespace WorkThrough;

public sealed record EnqueueOptions(
    string Queue = "default", string? CorrelationId = null, DateTimeOffset? NotBefore = null,
    int MaxAttempts = 3, string? IdempotencyKey = null, DateTimeOffset? ExpiresAt = null);

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
    public Task<Guid> EnqueueAsync<TInput>(JobContractId processor, TInput job, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        return store.EnqueueAsync(CreateRecord(processor, job, options), cancellationToken);
    }

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

        if (options.IdempotencyKey is { Length: 0 })
        {
            throw new ArgumentException("IdempotencyKey must be non-empty when supplied.", nameof(options));
        }

        var now = DateTimeOffset.UtcNow;
        // Immediate work has no not-before boundary. Use the current storage-resolution instant;
        // explicit NotBefore values keep their precision and are rounded up by the provider.
        var available = options.NotBefore ?? DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var payload = JsonSerializer.SerializeToElement(job, JobJson.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > 1_048_576)
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
            options.IdempotencyKey, ExpiresAt: options.ExpiresAt);
    }
}
