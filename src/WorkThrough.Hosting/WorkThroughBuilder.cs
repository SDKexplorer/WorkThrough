using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace WorkThrough;

/// <summary>Composes storage, contract registration, and optional execution independently.</summary>
public sealed class WorkThroughBuilder
{
    internal WorkThroughBuilder(IServiceCollection services, JobCatalog catalog, StorageInitialization initialization)
    {
        Services = services;
        Catalog = catalog;
        Initialization = initialization;
    }

    public IServiceCollection Services { get; }
    internal JobCatalog Catalog { get; }
    internal StorageInitialization Initialization { get; }

    public WorkThroughBuilder AddJob<TJob, THandler>() where THandler : class, IJobHandler<TJob>
    {
        Catalog.Register<TJob, THandler>();
        Services.TryAddScoped<THandler>();

        return this;
    }

    /// <summary>Registers a processor by its attributed permanent identity.</summary>
    public WorkThroughBuilder AddProcessor<TInput, TProcessor>() where TProcessor : class, IProcessor<TInput>
    {
        Catalog.RegisterProcessor<TInput, TProcessor>();
        Services.TryAddScoped<TProcessor>();

        return this;
    }

    /// <summary>Binds a typed permanent address to an implementation at the composition root.</summary>
    public WorkThroughBuilder AddProcessor<TInput, TProcessor>(JobDefinition<TInput> definition, params JobContractId[] aliases)
        where TProcessor : class, IProcessor<TInput>
    {
        Catalog.RegisterProcessor<TInput, TProcessor>(definition, aliases);
        Services.TryAddScoped<TProcessor>();

        return this;
    }

    /// <summary>Binds an explicit permanent identity to an implementation.</summary>
    public WorkThroughBuilder AddProcessor<TInput, TProcessor>(JobContractId identity, params JobContractId[] aliases)
        where TProcessor : class, IProcessor<TInput> => AddProcessor<TInput, TProcessor>(new JobDefinition<TInput>(identity), aliases);

    public WorkThroughBuilder AddGeneratedJobs(Assembly assembly)
    {
        var marker = assembly.GetCustomAttribute<JobModuleAttribute>()
            ?? throw new InvalidOperationException($"No generated job manifest in {assembly.GetName().Name}. Reference WorkThrough.Generators or register handlers explicitly.");
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

        options.Validate(x => x.MaxConcurrentJobs > 0 && x.PollInterval >= TimeSpan.FromMilliseconds(10)
            && x.LeaseDuration >= TimeSpan.FromSeconds(1) && x.Queues.Length > 0
            && !(x.ExecutionTimeout <= TimeSpan.Zero) && !(x.ProgressTimeout <= TimeSpan.Zero)
            && x.PerJobLimits.All(y => y.Value >= 0), "Invalid worker concurrency, lease, or queue configuration.").ValidateOnStart();
        Services.AddHostedService<JobWorker>();

        return this;
    }

    public WorkThroughBuilder AddMiddleware<TMiddleware>() where TMiddleware : class, IJobMiddleware
    {
        Services.AddScoped<IJobMiddleware, TMiddleware>();

        return this;
    }

    public WorkThroughBuilder AddValidator<TInput, TValidator>() where TValidator : class, IJobValidator<TInput>
    {
        Services.AddScoped<IJobValidator<TInput>, TValidator>();
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobMiddleware, ValidationMiddleware<TInput>>());

        return this;
    }

    public WorkThroughBuilder AddObserver<TObserver>() where TObserver : class, IJobObserver
    {
        Services.AddScoped<IJobObserver, TObserver>();

        return this;
    }

    public WorkThroughBuilder UseRetryPolicy<TPolicy>() where TPolicy : class, IJobRetryPolicy
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

    public WorkThroughBuilder AddEventSubscriber<THandler>(string name, long startAfter = 0) where THandler : class, IExecutionEventHandler
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || startAfter < 0)
        {
            throw new ArgumentException("A subscriber needs a stable name and non-negative initial position.");
        }

        Services.TryAddScoped<THandler>();
        Services.AddSingleton<IHostedService>(sp => new ExecutionEventSubscriber<THandler>(name, startAfter,
            sp.GetRequiredService<IExecutionEvents>(), sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExecutionEventSubscriber<THandler>>>()));

        return this;
    }

    /// <summary>Runs schedule and signal dispatch independently of job execution.</summary>
    public WorkThroughBuilder AddScheduler()
    {
        Services.AddHostedService<JobScheduler>();

        return this;
    }

    /// <summary>Sets initial shared policies. Existing database policies survive restarts and deployments.</summary>
    public WorkThroughBuilder SeedClusterLimits(params ConcurrencyLimit[] limits)
    {
        Initialization.Limits.AddRange(limits);

        return this;
    }

    public WorkThroughBuilder InitializeStorageOnStart(bool enabled)
    {
        Initialization.Enabled = enabled;

        return this;
    }
}

public static class WorkThroughRegistration
{
    public static WorkThroughBuilder AddWorkThrough(this IServiceCollection services)
    {
        if (services.Any(x => x.ServiceType == typeof(JobCatalog)))
        {
            throw new InvalidOperationException("Call AddWorkThrough once per service collection.");
        }

        var catalog = new JobCatalog();
        var initialization = new StorageInitialization();
        services.AddSingleton(catalog);
        services.AddSingleton(initialization);
        services.AddSingleton<JobClient>();
        services.AddSingleton<WorkflowClient>();
        services.AddSingleton<DeploymentPreflight>();
        services.TryAddScoped<IJobRetryPolicy, DefaultRetryPolicy>();
        services.AddHostedService<JobStorageInitializer>();

        return new(services, catalog, initialization);
    }
}

internal sealed class StorageInitialization
{
    public bool Enabled { get; set; } = true;
    public List<ConcurrencyLimit> Limits { get; } = [];
}

internal sealed class JobStorageInitializer(IJobStore store, StorageInitialization initialization) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (initialization.Enabled)
        {
            await store.InitializeAsync(cancellationToken);
        }

        if (initialization.Limits.Count > 0)
        {
            await store.SeedLimitsAsync(initialization.Limits, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
