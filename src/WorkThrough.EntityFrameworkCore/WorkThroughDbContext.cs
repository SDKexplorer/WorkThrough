using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using WorkThrough.Storage;

// Extensions live in the WorkThrough namespace, so enqueueing from a DbContext needs no extra using directive.
namespace WorkThrough;

/// <summary>A job recorded on a DbContext. Its ID is known once SaveChanges has committed it.</summary>
public sealed class PendingJob
{
    internal PendingJob(JobRecord job, bool outbox)
    {
        Job = job;
        Outbox = outbox;
    }

    internal JobRecord Job { get; }
    internal bool Outbox { get; }

    /// <summary>
    /// The accepted job's ID after SaveChanges; the existing job's ID when its idempotency key was already used, or the outbox receipt for
    /// a request to another store. Null before the save.
    /// </summary>
    public Guid? JobId { get; internal set; }
}

public static class WorkThroughDbContextOptionsExtensions
{
    /// <summary>
    /// Commits jobs recorded with <see cref="WorkThroughDbContextExtensions.Enqueue"/> in the same transaction as the context's
    /// changes. <paramref name="store"/> must use the context's database; the job tables live beside the application's.
    /// </summary>
    public static DbContextOptionsBuilder UseWorkThrough(this DbContextOptionsBuilder options, RelationalJobStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        return options.AddInterceptors(new WorkThroughSaveChangesInterceptor(store));
    }

    /// <inheritdoc cref="UseWorkThrough(DbContextOptionsBuilder, RelationalJobStore)"/>
    public static DbContextOptionsBuilder<TContext> UseWorkThrough<TContext>(this DbContextOptionsBuilder<TContext> options, RelationalJobStore store)
        where TContext : DbContext => (DbContextOptionsBuilder<TContext>)UseWorkThrough((DbContextOptionsBuilder)options, store);
}

public static class WorkThroughDbContextExtensions
{
    /// <summary>
    /// Records a job to commit with the context's next <c>SaveChangesAsync</c>: the entities and the job are accepted together or not at
    /// all. Takes the definition's queue and attempt budget unless <paramref name="options"/> says otherwise.
    /// </summary>
    public static PendingJob Enqueue<TInput>(this DbContext context, JobDefinition<TInput> definition, TInput input, EnqueueOptions? options = null) =>
        Record(context, JobClient.CreateRecord(definition.Identity, input, options ?? definition.DefaultOptions), outbox: false);

    /// <summary>Records a delivery request to a separate job store, saved in this database's outbox with the context's changes.</summary>
    public static PendingJob Send<TInput>(this DbContext context, JobDefinition<TInput> definition, TInput input, EnqueueOptions? options = null) =>
        Record(context, JobClient.CreateRecord(definition.Identity, input, options ?? definition.DefaultOptions), outbox: true);

    private static PendingJob Record(DbContext context, JobRecord job, bool outbox)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.GetService<IDbContextOptions>().Extensions.OfType<CoreOptionsExtension>()
            .Any(extension => extension.Interceptors?.OfType<WorkThroughSaveChangesInterceptor>().Any() == true))
        {
            throw new InvalidOperationException($"Call UseWorkThrough(store) when configuring {context.GetType().Name} to enqueue jobs with it.");
        }

        var pending = new PendingJob(job, outbox);
        WorkThroughSaveChangesInterceptor.Pending(context).Add(pending);

        return pending;
    }
}

/// <summary>Writes a context's recorded jobs after its changes and before its transaction commits.</summary>
internal sealed class WorkThroughSaveChangesInterceptor(RelationalJobStore store) : SaveChangesInterceptor
{
    private sealed class State
    {
        public List<PendingJob> Jobs { get; } = [];
        public IDbContextTransaction? Owned { get; set; }
    }

    private static readonly ConditionalWeakTable<DbContext, State> States = new();

    internal static List<PendingJob> Pending(DbContext context) => States.GetOrCreateValue(context).Jobs;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Without a transaction of the caller's, EF would commit the changes on its own before the jobs are written.
        if (eventData.Context is { } context && States.TryGetValue(context, out var state) && state.Jobs.Count > 0
            && context.Database.CurrentTransaction is null)
        {
            state.Owned = await context.Database.BeginTransactionAsync(cancellationToken);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context || !States.TryGetValue(context, out var state) || state.Jobs.Count == 0)
        {
            return result;
        }

        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("WorkThrough jobs need the context's transaction, which ended before they were written.");
        var jobs = state.Jobs.ToArray();
        var accepted = await store.EnqueueInTransactionAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(),
            [.. jobs.Select(job => (job.Job, job.Outbox))], cancellationToken);
        if (state.Owned is { } owned)
        {
            await owned.CommitAsync(cancellationToken);
            await owned.DisposeAsync();
            state.Owned = null;
        }

        state.Jobs.Clear();
        for (var index = 0; index < jobs.Length; index++)
        {
            jobs[index].JobId = accepted[index];
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        // The jobs stay recorded, so a retried save writes them with the changes.
        if (eventData.Context is { } context && States.TryGetValue(context, out var state) && state.Owned is { } owned)
        {
            await owned.DisposeAsync();
            state.Owned = null;
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        eventData.Context is { } context && States.TryGetValue(context, out var state) && state.Jobs.Count > 0
            ? throw new InvalidOperationException("Jobs recorded on a DbContext commit with SaveChangesAsync; the synchronous SaveChanges cannot write them.")
            : result;
}
