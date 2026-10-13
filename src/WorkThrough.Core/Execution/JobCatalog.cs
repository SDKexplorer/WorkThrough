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

    public void Register<TInput, TJob>() where TJob : class, IJob<TInput> =>
        Register<TInput, TJob>(new JobDefinition<TInput>(JobIdentity.For<TJob>(), JobIdentity.DisplayName(typeof(TJob))));

    /// <summary>Binds an address to a job class at the composition root; a [Job] attribute on the class must agree with it.</summary>
    public void Register<TInput, TJob>(JobDefinition<TInput> definition, params JobContractId[] aliases)
        where TJob : class, IJob<TInput>
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(aliases);
        if (typeof(TJob).GetCustomAttribute<JobAttribute>() is { } attribute
            && new JobContractId(attribute.Key, attribute.Version) != definition.Identity)
        {
            throw new InvalidOperationException("Explicit and attributed job addresses must agree.");
        }

        var declared = typeof(TJob).GetCustomAttributes<JobAliasAttribute>().Select(a => new JobContractId(a.Key, a.Version));
        Add<TInput, TJob>(definition.Identity, definition.DisplayName, declared.Concat(aliases).Distinct().ToArray(),
            (job, input, context, ct) => job.ExecuteAsync(input, context, ct));
    }

    /// <summary>
    /// Binds an address to a delegate, for jobs too small to deserve a class. The address is explicit, so stored work never depends on
    /// the delegate's compiled name.
    /// </summary>
    public void Register<TInput>(JobDefinition<TInput> definition, Func<JobCall<TInput>, Task<JobResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);
        if (registrations.ContainsKey(definition.Identity))
        {
            throw new InvalidOperationException($"{definition.Identity} already has a job.");
        }

        Add<TInput, DelegateJobHandler>(definition.Identity, definition.DisplayName, [],
            (handler, input, context, ct) => execute(new(input, context, handler.Services, ct)));
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
            throw new InvalidOperationException("Job registration is closed after worker startup.");
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

    /// <summary>
    /// Serves stored work at <paramref name="from"/> with the job already registered for <paramref name="to"/>, converting each stored
    /// input first. Work keeps its stored address and input; only how this build executes it changes. Both addresses are versions of one
    /// key, so they share admission limits. Use <see cref="JsonElement"/>
    /// as <typeparamref name="TOld"/> once the old input type no longer exists. Upcasts chain: <paramref name="to"/> may itself be one.
    /// </summary>
    public void Upcast<TOld, TNew>(JobContractId from, JobDefinition<TNew> to, Func<TOld, TNew> convert)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(convert);
        if (sealedCatalog)
        {
            throw new InvalidOperationException("Job registration is closed after worker startup.");
        }

        // Renames across keys use registry aliases, which fence the fleet; an upcast only bridges versions of one key.
        if (from.Key != to.Identity.Key || from.Version == to.Identity.Version)
        {
            throw new ArgumentException("An upcast bridges two versions of the same job key. Use a registry alias to change the key.", nameof(from));
        }

        var target = registrations.GetValueOrDefault(to.Identity)
            ?? throw new InvalidOperationException($"Register the job for {to.Identity} before upcasting {from} to it.");
        if (registrations.ContainsKey(from))
        {
            throw new InvalidOperationException($"{from} already has a job; remove it before serving that address through an upcast.");
        }

        registrations.Add(from, new(from, typeof(TOld), target.HandlerType, async (handler, payload, context, ct) =>
        {
            var stored = payload.Deserialize<TOld>(JobJson.Options) ?? throw new JsonException($"Null input is invalid for {from}.");
            var converted = JsonSerializer.SerializeToElement(convert(stored), JobJson.Options);

            return await target.InvokeAsync(handler, converted, context, ct).ConfigureAwait(false);
        })
        {
            DisplayName = target.DisplayName,
            UpcastTo = to.Identity
        });
    }

    public JobRegistration Get(JobContractId identity) => registrations.TryGetValue(identity, out var registration)
        ? registration : throw new InvalidOperationException($"No job is registered for {identity}.");
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

    /// <summary>The address whose job runs this one's stored work after converting its input, for an upcast; otherwise null.</summary>
    public JobContractId? UpcastTo { get; init; }
}
