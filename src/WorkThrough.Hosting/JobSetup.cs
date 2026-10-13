namespace WorkThrough;

/// <summary>
/// Everything about one job in one place: what runs it, when it recurs and how much of it may run. Created by
/// <see cref="WorkThroughBuilder.Job{TInput}(JobDefinition{TInput}, Action{JobSetup{TInput}})"/>.
/// </summary>
public sealed class JobSetup<TInput>
{
    private readonly WorkThroughBuilder builder;
    private readonly JobDefinition<TInput> definition;
    private bool handled;

    internal JobSetup(WorkThroughBuilder builder, JobDefinition<TInput> definition)
    {
        this.builder = builder;
        this.definition = definition;
    }

    /// <summary>Runs the job with <typeparamref name="TJob"/>, resolved from DI for each attempt.</summary>
    public JobSetup<TInput> RunWith<TJob>() where TJob : class, IJob<TInput> => Handle(() => builder.AddJob<TInput, TJob>(definition));

    /// <summary>Runs the job with a delegate; the job succeeds when it completes.</summary>
    public JobSetup<TInput> Run(Func<JobCall<TInput>, Task> execute) => Handle(() => builder.AddJob(definition, execute));

    /// <summary>Runs the job with a delegate whose result can carry an output, or reject or defer the work.</summary>
    public JobSetup<TInput> Run(Func<JobCall<TInput>, Task<JobResult>> execute) => Handle(() => builder.AddJob(definition, execute));

    /// <summary>
    /// Enqueues <paramref name="input"/> on the five-field <paramref name="cron"/> expression, in <paramref name="timeZone"/>. The schedule is
    /// named after the job unless <paramref name="key"/> names it; give each further schedule of the job its own key.
    /// </summary>
    public JobSetup<TInput> Every(string cron, TInput input, string timeZone = ScheduleDefinition.DefaultTimeZone, string? key = null)
    {
        builder.SeedSchedules(ScheduleDefinition.Create(key ?? definition.Identity.Key, cron, definition, input, timeZone));

        return this;
    }

    /// <summary>Admits at most <paramref name="maximum"/> attempts of this job at once across every worker.</summary>
    public JobSetup<TInput> AtMostRunning(int maximum)
    {
        builder.SeedClusterLimits(new ConcurrencyLimit(definition.Identity.Key, maximum));

        return this;
    }

    /// <summary>Admits at most <paramref name="maximum"/> attempts of this job at once per partition key; 1 serializes each key.</summary>
    public JobSetup<TInput> AtMostPerPartition(int maximum)
    {
        builder.SeedKeyedLimits(new KeyedLimit(KeyScope.Partition, definition.Identity.Key, maximum));

        return this;
    }

    /// <summary>Admits at most <paramref name="maximum"/> attempts of this job at once per tenant.</summary>
    public JobSetup<TInput> AtMostPerTenant(int maximum)
    {
        builder.SeedKeyedLimits(new KeyedLimit(KeyScope.Tenant, definition.Identity.Key, maximum));

        return this;
    }

    /// <summary>Starts at most <paramref name="permits"/> attempts of this job per sliding <paramref name="window"/>, optionally per key.</summary>
    public JobSetup<TInput> RateLimit(int permits, TimeSpan window, KeyScope? perKey = null)
    {
        builder.SeedRateLimits(new RateLimit(definition.Identity.Key, permits, window, perKey));

        return this;
    }

    private JobSetup<TInput> Handle(Action register)
    {
        if (handled)
        {
            throw new InvalidOperationException($"{definition.Identity} already has a job.");
        }

        register();
        handled = true;

        return this;
    }
}
