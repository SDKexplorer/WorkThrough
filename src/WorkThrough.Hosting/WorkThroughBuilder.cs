using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>Composes storage, contract registration, and optional execution independently.</summary>
public sealed class WorkThroughBuilder
{
    /// <summary>Polling interval of the schedule, signal, outbox and event-subscription services.</summary>
    internal static readonly TimeSpan DispatchInterval = TimeSpan.FromSeconds(1);

    private const int MaximumSubscriberLength = 200;
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(1);

    internal WorkThroughBuilder(IServiceCollection services, JobCatalog catalog, StorageInitialization initialization)
    {
        Services = services;
        Catalog = catalog;
        Initialization = initialization;
    }

    public IServiceCollection Services { get; }
    internal JobCatalog Catalog { get; }
    internal StorageInitialization Initialization { get; }

    /// <summary>Registers one store instance as every storage and operations port it implements.</summary>
    public WorkThroughBuilder UseStore<TStore>(TStore store)
        where TStore : class, IJobStore, IJobQueries, IExecutionQueries, IExecutionInspectionQueries, IOperationHistoryQueries,
        IRuntimeQueries, IRetentionQueries, IRetentionAdministration, IRetentionMaintenance, IJobAdministration, IWorkflowDefinitions,
        IJobDiagnostics, IJobSchedules, IDeploymentQueries, IProcessorRegistry, IExecutionOperations, IExecutionArchives,
        IWorkflowSignals, IJobOutbox, IExecutionEvents, IAdmissionPolicies
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Initialization.PayloadProtector is { } protector)
        {
            Protect(store, protector);
        }

        Services.AddSingleton(store);
        Type[] ports =
        [
            typeof(IJobStore), typeof(IJobQueries), typeof(IExecutionQueries), typeof(IExecutionInspectionQueries), typeof(IOperationHistoryQueries),
            typeof(IRuntimeQueries), typeof(IRetentionQueries), typeof(IRetentionAdministration), typeof(IRetentionMaintenance),
            typeof(IJobAdministration), typeof(IWorkflowDefinitions), typeof(IJobDiagnostics), typeof(IJobSchedules), typeof(IDeploymentQueries),
            typeof(IProcessorRegistry), typeof(IExecutionOperations), typeof(IExecutionArchives), typeof(IWorkflowSignals), typeof(IJobOutbox),
            typeof(IExecutionEvents), typeof(IAdmissionPolicies)
        ];
        foreach (var port in ports)
        {
            Services.AddSingleton(port, provider => provider.GetRequiredService<TStore>());
        }

        if (store is IScheduleCalendars)
        {
            Services.AddSingleton(provider => (IScheduleCalendars)provider.GetRequiredService<TStore>());
        }

        return this;
    }

    /// <summary>Registers a job at the address its [Job] attribute, or its input's [JobContract], declares.</summary>
    public WorkThroughBuilder AddJob<TInput, [DynamicallyAccessedMembers(JobIdentity.JobTypeMembers)] TJob>() where TJob : class, IJob<TInput>
    {
        Catalog.Register<TInput, TJob>();
        Services.TryAddScoped<TJob>();

        return this;
    }

    /// <summary>Binds a typed permanent address to a job class at the composition root.</summary>
    public WorkThroughBuilder AddJob<TInput, [DynamicallyAccessedMembers(JobIdentity.JobTypeMembers)] TJob>(JobDefinition<TInput> definition, params JobContractId[] aliases)
        where TJob : class, IJob<TInput>
    {
        Catalog.Register<TInput, TJob>(definition, aliases);
        Services.TryAddScoped<TJob>();

        return this;
    }

    /// <summary>Binds an explicit permanent address to a job class.</summary>
    public WorkThroughBuilder AddJob<TInput, [DynamicallyAccessedMembers(JobIdentity.JobTypeMembers)] TJob>(JobContractId identity, params JobContractId[] aliases)
        where TJob : class, IJob<TInput> => AddJob<TInput, TJob>(new JobDefinition<TInput>(identity), aliases);

    /// <summary>
    /// Sets up one job: what runs it, its schedules and its limits. Seeded schedules and limits are created once; operators own them afterwards.
    /// </summary>
    /// <example>
    /// <code>
    /// .Job(Reports.Daily, job =&gt; job
    ///     .RunWith&lt;DailyReportJob&gt;()
    ///     .Every("0 6 * * *", new DailyReport(Days: 1), "Europe/Bucharest")
    ///     .AtMostRunning(1))
    /// </code>
    /// </example>
    public WorkThroughBuilder Job<TInput>(JobDefinition<TInput> definition, Action<JobSetup<TInput>> setup)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(setup);
        setup(new(this, definition));

        return this;
    }

    /// <summary>Runs <paramref name="execute"/> for work at the definition's address; the job succeeds when it completes.</summary>
    /// <example><c>.AddJob(Emails.Welcome, job =&gt; job.Service&lt;IMailer&gt;().SendAsync(job.Input.Address, job.CancellationToken))</c></example>
    public WorkThroughBuilder AddJob<TInput>(JobDefinition<TInput> definition, Func<JobCall<TInput>, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        return AddJob(definition, async job =>
        {
            await execute(job).ConfigureAwait(false);

            return JobResult.Done();
        });
    }

    /// <summary>Runs <paramref name="execute"/> for work at the definition's address; its result can carry an output or reject the work.</summary>
    public WorkThroughBuilder AddJob<TInput>(JobDefinition<TInput> definition, Func<JobCall<TInput>, Task<JobResult>> execute)
    {
        Catalog.Register(definition, execute);
        Services.TryAddScoped<DelegateJobHandler>();

        return this;
    }

    /// <summary>Runs <paramref name="execute"/> for work at the address of <typeparamref name="TInput"/>'s [JobContract].</summary>
    public WorkThroughBuilder AddJob<TInput>(Func<JobCall<TInput>, Task> execute) =>
        AddJob(new JobDefinition<TInput>(JobContractId.For<TInput>()), execute);

    /// <summary>
    /// Registers every job class in the assembly at the address its [Job] attribute, or its input's [JobContract], declares. It
    /// follows the source generator's rules at startup instead of build time. Classes marked [ManualJobRegistration] are left to AddJob.
    /// </summary>
    [RequiresUnreferencedCode("Scans the assembly for job classes; use the source generator's AddGeneratedJobs or AddJob when trimming.")]
    [RequiresDynamicCode("Registers each job class found by reflection; use the source generator's AddGeneratedJobs or AddJob in native AOT.")]
    public WorkThroughBuilder AddJobs(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var register = typeof(WorkThroughBuilder).GetMethods().Single(method => method.Name == nameof(AddJob) && method.GetParameters().Length == 0);
        var jobs = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && type.GetCustomAttribute<ManualJobRegistrationAttribute>() is null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var inputs = job.GetInterfaces().Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJob<>))
                .Select(type => type.GetGenericArguments()[0]);
            foreach (var input in inputs)
            {
                if (job.GetCustomAttribute<JobAttribute>() is null && input.GetCustomAttribute<JobContractAttribute>() is null)
                {
                    throw new InvalidOperationException($"Job {job.FullName} needs a [Job] address, or an input type with a [JobContract] address. " +
                        "Mark it [ManualJobRegistration] to register it with AddJob instead.");
                }

                register.MakeGenericMethod(input, job).Invoke(this, BindingFlags.DoNotWrapExceptions, null, null, null);
            }
        }

        return this;
    }

    /// <summary>
    /// Serves stored work at an older version of a job's address with the job registered for <paramref name="to"/>, converting each
    /// stored input first. Register the target job before its upcasts.
    /// </summary>
    /// <example><c>.AddUpcast(new JobContractId("invoices.generate", 1), InvoiceJobs.Generate, (InvoiceV1 old) =&gt; new GenerateInvoice(old.Id, "EUR"))</c></example>
    public WorkThroughBuilder AddUpcast<TOld, TNew>(JobContractId from, JobDefinition<TNew> to, Func<TOld, TNew> convert)
    {
        Catalog.Upcast(from, to, convert);

        return this;
    }

    public WorkThroughBuilder AddGeneratedJobs(Assembly assembly)
    {
        var marker = assembly.GetCustomAttribute<JobModuleAttribute>()
            ?? throw new InvalidOperationException($"No generated job manifest in {assembly.GetName().Name}. Reference WorkThrough.Generators or register jobs explicitly with AddJob.");
        var module = (IJobModule)Activator.CreateInstance(marker.ModuleType)!;
        module.Register(Catalog);
        foreach (var registration in Catalog.Registrations)
        {
            Services.TryAddScoped(registration.HandlerType);
        }

        return this;
    }

    public WorkThroughBuilder AddWorker(Action<JobWorkerOptions>? configure = null)
    {
        var options = Services.AddOptions<JobWorkerOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.Validate(x => x.MaxConcurrentJobs > 0 && x.PollInterval >= MinimumPollInterval
            && x.LeaseDuration >= MinimumLease && x.Queues.Length > 0
            && !(x.ExecutionTimeout <= TimeSpan.Zero) && !(x.ProgressTimeout <= TimeSpan.Zero)
            && x.PerJobLimits.All(y => y.Value >= 0), "Invalid worker concurrency, lease, or queue configuration.").ValidateOnStart();
        Services.TryAddSingleton<WorkerStatus>();
        Services.AddHostedService<JobWorker>();

        return this;
    }

    public WorkThroughBuilder AddMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>() where TMiddleware : class, IJobMiddleware
    {
        Services.AddScoped<IJobMiddleware, TMiddleware>();

        return this;
    }

    public WorkThroughBuilder AddValidator<TInput, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>() where TValidator : class, IJobValidator<TInput>
    {
        Services.AddScoped<IJobValidator<TInput>, TValidator>();
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobMiddleware, ValidationMiddleware<TInput>>());

        return this;
    }

    public WorkThroughBuilder AddObserver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TObserver>() where TObserver : class, IJobObserver
    {
        Services.AddScoped<IJobObserver, TObserver>();

        return this;
    }

    public WorkThroughBuilder UseRetryPolicy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>() where TPolicy : class, IJobRetryPolicy
    {
        Services.Replace(ServiceDescriptor.Scoped<IJobRetryPolicy, TPolicy>());

        return this;
    }

    /// <summary>Delivers from a separately configured source outbox into this worker's job store.</summary>
    public WorkThroughBuilder AddOutboxDispatcher(IJobOutbox source)
    {
        Services.AddSingleton(source);
        Services.AddHostedService<JobOutboxDispatcher>();

        return this;
    }

    public WorkThroughBuilder AddEventSubscriber<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(string name, long startAfter = 0) where THandler : class, IExecutionEventHandler
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumSubscriberLength || startAfter < 0)
        {
            throw new ArgumentException("A subscriber needs a stable name and non-negative initial position.");
        }

        Services.TryAddScoped<THandler>();
        Services.AddSingleton<IHostedService>(sp => new ExecutionEventSubscriber<THandler>(name, startAfter,
            sp.GetRequiredService<IExecutionEvents>(), sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<ExecutionEventSubscriber<THandler>>>()));

        return this;
    }

    /// <summary>Runs schedule and signal dispatch independently of job execution.</summary>
    public WorkThroughBuilder AddScheduler()
    {
        Services.AddHostedService<JobScheduler>();
        Initialization.SchedulerHosted = true;

        return this;
    }

    /// <summary>Sets initial shared policies. Existing database policies survive restarts and deployments.</summary>
    public WorkThroughBuilder SeedClusterLimits(params ConcurrencyLimit[] limits)
    {
        Initialization.Limits.AddRange(limits);

        return this;
    }

    /// <summary>Sets initial partition-key and tenant limits. Existing database policies survive restarts and deployments.</summary>
    public WorkThroughBuilder SeedKeyedLimits(params KeyedLimit[] limits)
    {
        Initialization.KeyedLimits.AddRange(limits);

        return this;
    }

    /// <summary>Sets initial rate limits. Existing database policies survive restarts and deployments.</summary>
    public WorkThroughBuilder SeedRateLimits(params RateLimit[] limits)
    {
        Initialization.RateLimits.AddRange(limits);

        return this;
    }

    /// <summary>
    /// Creates, publishes and enables these schedules at startup when their key and version do not exist yet. Existing schedules,
    /// including ones operators disabled or changed, are never modified. Requires <see cref="AddScheduler"/> somewhere in the fleet.
    /// </summary>
    public WorkThroughBuilder SeedSchedules(params ScheduleDefinition[] schedules)
    {
        foreach (var schedule in schedules)
        {
            ScheduleClock.Validate(schedule);
        }

        Initialization.Schedules.AddRange(schedules);

        return this;
    }

    /// <summary>
    /// Encrypts job inputs and outputs, flow inputs and signal payloads at rest with AES-256-GCM (ADR-027). New values use
    /// <paramref name="currentKey"/>; keep retired keys in <paramref name="keys"/> while stored work still uses them. Every host that runs
    /// or advances work needs the same key ring. Encryption starts once engine protocol 7 is active.
    /// </summary>
    public WorkThroughBuilder ProtectPayloads(string currentKey, IReadOnlyDictionary<string, byte[]> keys) =>
        ProtectPayloads(new AesGcmPayloadProtector(keys, currentKey));

    /// <summary>Encrypts business data at rest with a custom protector, for example one backed by a key management service.</summary>
    public WorkThroughBuilder ProtectPayloads(IPayloadProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        Initialization.PayloadProtector = protector;
        foreach (var store in Services.Where(service => !service.IsKeyedService).Select(service => service.ImplementationInstance).OfType<IJobStore>())
        {
            Protect(store, protector);
        }

        return this;
    }

    private static void Protect(object store, IPayloadProtector protector)
    {
        if (store is not IPayloadProtection protection)
        {
            throw new NotSupportedException($"{store.GetType().Name} cannot protect payloads.");
        }

        protection.PayloadProtector = protector;
    }

    public WorkThroughBuilder InitializeStorageOnStart(bool enabled)
    {
        Initialization.Enabled = enabled;

        return this;
    }
}
