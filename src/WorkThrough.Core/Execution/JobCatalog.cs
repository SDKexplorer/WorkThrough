using System.Reflection;
using System.Text.Json;

namespace WorkThrough;

/// <summary>The single registry used by explicit and generated registration.</summary>
public sealed class JobCatalog : IJobRegistrar
{
    private readonly Dictionary<JobContractId, JobRegistration> registrations = [];
    private bool sealedCatalog;
    public IReadOnlyCollection<JobRegistration> Registrations => registrations.Values;
    public IReadOnlyList<ProcessorAliasMapping> Aliases => registrations.Values.Where(r => r.Identity != r.CanonicalIdentity)
        .Select(r => new ProcessorAliasMapping(r.Identity, r.CanonicalIdentity)).OrderBy(r => r.Alias.Key, StringComparer.Ordinal).ThenBy(r => r.Alias.Version).ToArray();

    public void Register<TJob, THandler>() where THandler : class, IJobHandler<TJob> =>
        Add<TJob, THandler>(JobContractId.For<TJob>(), null, [], (handler, input, context, ct) => handler.HandleAsync(input, context, ct));

    public void RegisterProcessor<TInput, TProcessor>() where TProcessor : class, IProcessor<TInput> =>
        RegisterProcessor<TInput, TProcessor>(new JobDefinition<TInput>(ProcessorId.For<TProcessor>(),
            typeof(TProcessor).GetCustomAttribute<ProcessorAttribute>()?.DisplayName));

    /// <summary>Registers behavior independently from implementation attributes.</summary>
    public void RegisterProcessor<TInput, TProcessor>(JobDefinition<TInput> definition, params JobContractId[] aliases)
        where TProcessor : class, IProcessor<TInput>
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(aliases);
        if (typeof(TProcessor).GetCustomAttribute<ProcessorAttribute>() is { } attribute
            && new JobContractId(attribute.Id, attribute.Version) != definition.Identity)
        {
            throw new InvalidOperationException("Explicit and attributed processor identities must agree.");
        }

        var declared = typeof(TProcessor).GetCustomAttributes<ProcessorAliasAttribute>().Select(a => new JobContractId(a.Id, a.Version));
        Add<TInput, TProcessor>(definition.Identity, definition.DisplayName, declared.Concat(aliases).Distinct().ToArray(),
            (processor, input, context, ct) => processor.ProcessAsync(input, context, ct));
    }

    private void Add<TJob, THandler>(JobContractId identity, string? displayName, JobContractId[] aliases,
        Func<THandler, TJob, JobExecutionContext, CancellationToken, Task<JobResult>> invoke)
    {
        if (identity.Key == SignalWait.Processor.Key || aliases.Any(alias => alias?.Key == SignalWait.Processor.Key))
        {
            throw new ArgumentException("The signal processor address is reserved for the engine.");
        }

        if (sealedCatalog)
        {
            throw new InvalidOperationException("Processor registration is closed after worker startup.");
        }

        if (aliases.Any(a => a is null || a == identity))
        {
            throw new ArgumentException("Aliases must be non-null historical identities distinct from the target.");
        }

        var candidates = aliases.Prepend(identity).ToArray();
        foreach (var candidate in candidates)
        {
            if (registrations.TryGetValue(candidate, out var existing)
                && (existing.HandlerType != typeof(THandler) || existing.RequestType != typeof(TJob) || existing.CanonicalIdentity != identity))
            {
                throw new InvalidOperationException($"Conflicting handlers or aliases for {candidate}.");
            }

            if (registrations.Values.Any(r => r.Identity.Key == candidate.Key && r.CanonicalIdentity.Key != identity.Key))
            {
                throw new InvalidOperationException("Every version of a processor key must share one admission group.");
            }
        }

        foreach (var candidate in candidates)
        {
            registrations.TryAdd(candidate, new(candidate, typeof(TJob), typeof(THandler), async (handler, payload, context, ct) =>
            {
                var request = payload.Deserialize<TJob>(JobJson.Options) ?? throw new JsonException($"Null input is invalid for {candidate}.");

                return await invoke((THandler)handler, request, context, ct).ConfigureAwait(false);
            })
            {
                CanonicalIdentity = identity,
                DisplayName = displayName
            });
        }
    }

    public JobRegistration Get(JobContractId identity) => registrations.TryGetValue(identity, out var registration)
        ? registration : throw new InvalidOperationException($"No handler is registered for {identity}.");
    public string CapacityKey(string key) => registrations.Values.FirstOrDefault(r => r.Identity.Key == key)?.CanonicalIdentity.Key ?? key;
    public void Seal() => sealedCatalog = true;

    /// <summary>Rejects an unactivated alias or an obsolete interpretation before work is acquired.</summary>
    public void ValidateRegistry(ProcessorRegistrySnapshot snapshot)
    {
        foreach (var registration in registrations.Values)
        {
            var persisted = snapshot.Aliases.SingleOrDefault(a => a.Alias == registration.Identity)?.Target ?? registration.Identity;
            if (persisted != registration.CanonicalIdentity)
            {
                throw new InvalidOperationException($"Processor mapping for {registration.Identity} differs from registry revision {snapshot.Revision}. Apply the reviewed registry and restart the worker.");
            }
        }
    }
}

public sealed record JobRegistration(JobContractId Identity, Type RequestType, Type HandlerType,
    Func<object, JsonElement, JobExecutionContext, CancellationToken, Task<JobResult>> InvokeAsync)
{
    public JobContractId CanonicalIdentity { get; init; } = Identity;
    public string? DisplayName { get; init; }
}
