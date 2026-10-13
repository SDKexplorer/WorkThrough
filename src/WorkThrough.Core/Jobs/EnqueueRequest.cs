using System.Runtime.CompilerServices;

namespace WorkThrough;

/// <summary>
/// One job about to be enqueued. Each method returns a refined copy; awaiting the request, or calling <see cref="SendAsync"/>, commits it.
/// </summary>
public sealed class EnqueueRequest<TInput>
{
    private readonly JobClient client;
    private readonly JobContractId address;
    private readonly TInput input;

    internal EnqueueRequest(JobClient client, JobContractId address, TInput input, EnqueueOptions options)
    {
        this.client = client;
        this.address = address;
        this.input = input;
        Options = options;
    }

    /// <summary>The options the request will be enqueued with.</summary>
    public EnqueueOptions Options { get; }

    /// <summary>Runs no earlier than <paramref name="delay"/> from now.</summary>
    public EnqueueRequest<TInput> After(TimeSpan delay) => delay < TimeSpan.Zero
        ? throw new ArgumentOutOfRangeException(nameof(delay), "A delay cannot be negative.")
        : With(Options with { NotBefore = DateTimeOffset.UtcNow + delay });

    /// <summary>Runs no earlier than <paramref name="instant"/>.</summary>
    public EnqueueRequest<TInput> At(DateTimeOffset instant) => With(Options with { NotBefore = instant });

    /// <summary>Accepts the work once for <paramref name="idempotencyKey"/>: repeating the request returns the same job.</summary>
    public EnqueueRequest<TInput> Idempotent(string idempotencyKey) => With(Options with { IdempotencyKey = idempotencyKey });

    public EnqueueRequest<TInput> InQueue(string queue) => With(Options with { Queue = queue });

    public EnqueueRequest<TInput> WithMaxAttempts(int attempts) => With(Options with { MaxAttempts = attempts });

    /// <summary>Groups the job with related work for searches; never a lock or deduplication key.</summary>
    public EnqueueRequest<TInput> Correlate(string correlationId) => With(Options with { CorrelationId = correlationId });

    /// <summary>Counts the job against tenant <paramref name="tenantId"/>'s limits and scopes it to that tenant's operators.</summary>
    public EnqueueRequest<TInput> ForTenant(string tenantId) => With(Options with { TenantId = tenantId });

    /// <summary>Counts the job against partition limits for <paramref name="partitionKey"/>, for example one customer at a time.</summary>
    public EnqueueRequest<TInput> InPartition(string partitionKey) => With(Options with { PartitionKey = partitionKey });

    /// <summary>Expires the job if no attempt starts before <paramref name="instant"/>.</summary>
    public EnqueueRequest<TInput> ExpiresAt(DateTimeOffset instant) => With(Options with { ExpiresAt = instant });

    /// <summary>Commits the job and returns its ID once storage has accepted it.</summary>
    public Task<Guid> SendAsync(CancellationToken cancellationToken = default) => client.EnqueueAsync(address, input, Options, cancellationToken);

    public TaskAwaiter<Guid> GetAwaiter() => SendAsync().GetAwaiter();

    private EnqueueRequest<TInput> With(EnqueueOptions options) => new(client, address, input, options);
}
