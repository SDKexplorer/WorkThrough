using System.Text.Json;

namespace WorkThrough;

/// <summary>Options for one flow run. The partition key applies to every step of the run.</summary>
public sealed record FlowStart(string? CorrelationId = null, string? IdempotencyKey = null, string? PartitionKey = null)
{
    public void Validate()
    {
        if (IdempotencyKey is not null)
        {
            WorkflowClient.ValidateIdempotencyKey(IdempotencyKey);
        }

        PartitionKeys.Validate(PartitionKey, nameof(PartitionKey));
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

        return signals.SignalAsync(runId, signal.Name, JsonSerializer.SerializeToElement(input, JobJson.Options), idempotencyKey, cancellationToken);
    }

    /// <summary>Starts a new run. Correlation groups observations and does not deduplicate requests.</summary>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);

        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), correlationId, cancellationToken);
    }

    /// <summary>Returns the same logical run when an identical request is repeated with the same store-scoped key.</summary>
    /// <remarks>Reusing a key with different serialized definition, input, or correlation fails. Effects remain at least once.</remarks>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, string? correlationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        FlowValidator.Validate(definition);
        ValidateIdempotencyKey(idempotencyKey);

        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), correlationId, idempotencyKey, cancellationToken);
    }

    /// <summary>Starts a run with explicit start options, such as an admission partition shared by all steps.</summary>
    public Task<Guid> StartAsync<T>(FlowDefinition definition, T input, FlowStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        FlowValidator.Validate(definition);
        start.Validate();

        return store.StartWorkflowAsync(definition, JsonSerializer.SerializeToElement(input, JobJson.Options), start, cancellationToken);
    }

    /// <summary>Validates the store-scoped flow-start request key.</summary>
    public static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException("Flow idempotency keys must contain 1–256 characters and cannot be whitespace.", nameof(idempotencyKey));
        }
    }
}
