using System.Text.Json;

namespace WorkThrough;

/// <summary>The single registry used by explicit and generated registration.</summary>
public sealed class JobCatalog : IJobRegistrar
{
    private readonly Dictionary<JobContractId, JobRegistration> registrations = [];
    public IReadOnlyCollection<JobRegistration> Registrations => registrations.Values;

    public void Register<TJob, THandler>() where THandler : class, IJobHandler<TJob>
    {
        var identity = JobContractId.For<TJob>();
        Add<TJob, THandler>(identity, async (handler, input, context, ct) =>
            await handler.HandleAsync(input, context, ct).ConfigureAwait(false));
    }

    public void RegisterProcessor<TInput, TProcessor>() where TProcessor : class, IProcessor<TInput>
    {
        var identity = ProcessorId.For<TProcessor>();
        Add<TInput, TProcessor>(identity, async (processor, input, context, ct) =>
            await processor.ProcessAsync(input, context, ct).ConfigureAwait(false));
    }

    private void Add<TJob, THandler>(JobContractId identity,
        Func<THandler, TJob, JobExecutionContext, CancellationToken, Task<JobResult>> invoke)
    {
        if (registrations.TryGetValue(identity, out var existing))
        {
            if (existing.HandlerType == typeof(THandler) && existing.RequestType == typeof(TJob)) return;
            throw new InvalidOperationException($"Conflicting handlers for {identity}: {existing.HandlerType.FullName} and {typeof(THandler).FullName}.");
        }

        registrations.Add(identity, new(identity, typeof(TJob), typeof(THandler), async (handler, payload, context, ct) =>
        {
            var request = payload.Deserialize<TJob>(JobJson.Options)
                ?? throw new JsonException($"Null input is invalid for {identity}.");
            return await invoke((THandler)handler, request, context, ct).ConfigureAwait(false);
        }));
    }

    public JobRegistration Get(JobContractId identity) => registrations.TryGetValue(identity, out var registration)
        ? registration
        : throw new InvalidOperationException($"No handler is registered for {identity}.");
}

public sealed record JobRegistration(
    JobContractId Identity,
    Type RequestType,
    Type HandlerType,
    Func<object, JsonElement, JobExecutionContext, CancellationToken, Task<JobResult>> InvokeAsync);
