using System.Text.Json;

namespace WorkThrough;

/// <summary>Options for one flow run. The partition key and tenant ID apply to every step of the run.</summary>
/// <remarks>MaxParallelSteps bounds how many of the run's steps hold admitted leases at once, so a wide fan-out releases its
/// branches gradually instead of occupying every worker slot. Null leaves the run bounded only by other limits.</remarks>
public sealed record FlowStart(string? CorrelationId = null, string? IdempotencyKey = null, string? PartitionKey = null,
    string? TenantId = null, int? MaxParallelSteps = null)
{
    public void Validate()
    {
        if (IdempotencyKey is not null)
        {
            WorkflowClient.ValidateIdempotencyKey(IdempotencyKey);
        }

        AdmissionKeys.Validate(PartitionKey, KeyScope.Partition, nameof(PartitionKey));
        AdmissionKeys.Validate(TenantId, KeyScope.Tenant, nameof(TenantId));
        if (MaxParallelSteps is < 1 or > FlowValidator.MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxParallelSteps), $"MaxParallelSteps must be 1–{FlowValidator.MaximumSteps}.");
        }
    }
}

public sealed class WorkflowClient(IJobStore store)
{
    public Task<bool> SignalAsync<TInput>(Guid runId, SignalDefinition<TInput> signal, TInput input, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (store is not IWorkflowSignals signals)
        {
            throw new NotSupportedException("This provider does not support durable signals.");
        }

        return signals.SignalAsync(runId, signal.Name, JobJson.ToElement(input), idempotencyKey, cancellationToken);
    }

    /// <summary>Starts a new run. Correlation groups observations and does not deduplicate requests.</summary>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);

        return store.StartWorkflowAsync(definition, JobJson.ToElement(input), correlationId, cancellationToken);
    }

    /// <summary>Returns the same logical run when an identical request is repeated with the same store-scoped key.</summary>
    /// <remarks>Reusing a key with different serialized definition, input, or correlation fails. Effects remain at least once.</remarks>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);
        ValidateIdempotencyKey(idempotencyKey);

        return store.StartWorkflowAsync(definition, JobJson.ToElement(input), correlationId, idempotencyKey, cancellationToken);
    }

    /// <summary>Starts a run with explicit start options, such as an admission partition shared by all steps.</summary>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, FlowStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        FlowValidator.Validate(definition);
        start.Validate();

        return store.StartWorkflowAsync(definition, JobJson.ToElement(input), start, cancellationToken);
    }

    public const int MaximumIdempotencyKeyLength = 256;

    /// <summary>Validates the store-scoped flow-start request key.</summary>
    public static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            throw new ArgumentException($"Flow idempotency keys must contain 1–{MaximumIdempotencyKeyLength} characters and cannot be whitespace.", nameof(idempotencyKey));
        }
    }
}
