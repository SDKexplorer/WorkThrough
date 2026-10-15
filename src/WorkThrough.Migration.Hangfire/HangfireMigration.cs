using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;

namespace WorkThrough.Migration.Hangfire;

/// <summary>
/// Moves Hangfire's recurring, enqueued and scheduled jobs into WorkThrough. Map each Hangfire method to a WorkThrough address, review
/// the <see cref="Plan"/>, stop the Hangfire servers, <see cref="ImportAsync"/>, then <see cref="Retire"/> the imported jobs in
/// Hangfire. Reads go through Hangfire's own storage API, so any Hangfire storage works. Run it where the jobs' types load.
/// </summary>
public sealed partial class HangfireMigration(JobStorage storage)
{
    private const int PageSize = 500;
    private const string IdempotencyPrefix = "hangfire:";
    private const string SchedulePrefix = "hangfire.";
    private static readonly OperationActor Actor = new("workthrough", "hangfire-migration", "Hangfire migration");

    private readonly List<Mapping> mappings = [];
    private Func<string, string> scheduleKey = DefaultScheduleKey;

    private sealed record Mapping(Type Service, MethodInfo Method, JobContractId Target, Func<HangfireCall, JsonElement> Convert, string? Queue);

    private sealed record NameMapping(string Type, string Method, JobContractId Target, Func<HangfireArguments, JsonElement> Convert, string? Queue);

    private readonly List<NameMapping> nameMappings = [];
    private bool includeFailed;
    private const string AwaitingSet = "awaiting";

    /// <summary>Imports calls of <paramref name="method"/> as work at <paramref name="target"/>, with the input <paramref name="convert"/> builds.</summary>
    /// <example><c>.Map((IMailer mailer) =&gt; mailer.SendWelcome(default!), Emails.Welcome, call =&gt; new WelcomeEmail(call.Argument&lt;string&gt;(0)))</c></example>
    public HangfireMigration Map<TService, TInput>(Expression<Action<TService>> method, JobDefinition<TInput> target, Func<HangfireCall, TInput> convert,
        string? queue = null) => Add(typeof(TService), method.Body, target, convert, queue);

    /// <summary>Imports calls of an asynchronous <paramref name="method"/>.</summary>
    public HangfireMigration Map<TService, TInput>(Expression<Func<TService, Task>> method, JobDefinition<TInput> target, Func<HangfireCall, TInput> convert,
        string? queue = null) => Add(typeof(TService), method.Body, target, convert, queue);

    /// <summary>
    /// Imports calls of <paramref name="method"/> on the type whose full name is <paramref name="type"/>, read from Hangfire's stored
    /// invocation, so the type need not load. Typed mappings take precedence when the type does load.
    /// </summary>
    /// <example><c>.MapByName("MyApp.Mailer", "SendWelcome", Emails.Welcome, call =&gt; new WelcomeEmail(call.Argument&lt;string&gt;(0)))</c></example>
    public HangfireMigration MapByName<TInput>(string type, string method, JobDefinition<TInput> target, Func<HangfireArguments, TInput> convert,
        string? queue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(convert);
        nameMappings.Add(new(type, method, target.Identity, arguments => JsonSerializer.SerializeToElement(convert(arguments), JobJson.Options), queue));

        return this;
    }

    /// <summary>Also imports jobs that failed in Hangfire, to run again in WorkThrough with a fresh attempt budget.</summary>
    public HangfireMigration IncludeFailed()
    {
        includeFailed = true;

        return this;
    }

    /// <summary>Names the WorkThrough schedule for each Hangfire recurring job ID; by default <c>hangfire.</c> plus the ID in lowercase.</summary>
    public HangfireMigration NameSchedules(Func<string, string> key)
    {
        scheduleKey = key ?? throw new ArgumentNullException(nameof(key));

        return this;
    }

    private HangfireMigration Add<TInput>(Type service, Expression body, JobDefinition<TInput> target, Func<HangfireCall, TInput> convert, string? queue)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(convert);
        var method = body is MethodCallExpression call ? call.Method : throw new ArgumentException("Pass a method call, such as x => x.Send(default!).");
        mappings.Add(new(service, method, target.Identity, invocation => JsonSerializer.SerializeToElement(convert(invocation), JobJson.Options), queue));

        return this;
    }

    /// <summary>
    /// Reads every recurring, enqueued, scheduled and awaiting job and decides what each becomes. Changes nothing. A job with
    /// continuations becomes a flow: the job is the first step and each continuation runs after its parent. If any job in such a chain
    /// cannot move, the whole chain stays in Hangfire, so no continuation is left without its parent.
    /// </summary>
    public HangfireMigrationPlan Plan()
    {
        var items = new List<HangfireMigrationItem>();
        var continuations = new List<HangfireMigrationItem>();
        using (var connection = storage.GetConnection())
        {
            // Hangfire's dashboard lists continuations waiting for their parent from this set.
            foreach (var id in connection.GetAllItemsFromSet(AwaitingSet))
            {
                continuations.Add(Continuation(connection, id));
            }

            foreach (var recurring in connection.GetRecurringJobs())
            {
                var stored = connection.GetAllEntriesFromHash($"recurring-job:{recurring.Id}")?.GetValueOrDefault("Job") is { } payload
                    ? InvocationData.DeserializePayload(payload)
                    : null;
                items.Add(Recurring(recurring.Id, recurring.Job, stored, recurring.LoadException, recurring.Cron, recurring.TimeZoneId, recurring.Queue));
            }
        }

        var monitoring = storage.GetMonitoringApi();
        foreach (var queue in monitoring.Queues())
        {
            for (var from = 0; ; from += PageSize)
            {
                var page = monitoring.EnqueuedJobs(queue.Name, from, PageSize);
                items.AddRange(page.Where(entry => entry.Value.InEnqueuedState).Select(entry =>
                    Job(HangfireJobKind.Enqueued, entry.Key, entry.Value.Job, entry.Value.InvocationData, entry.Value.LoadException, queue.Name, null)));
                if (page.Count < PageSize)
                {
                    break;
                }
            }
        }

        for (var from = 0; ; from += PageSize)
        {
            var page = monitoring.ScheduledJobs(from, PageSize);
            items.AddRange(page.Where(entry => entry.Value.InScheduledState).Select(entry =>
                Job(HangfireJobKind.Scheduled, entry.Key, entry.Value.Job, entry.Value.InvocationData, entry.Value.LoadException,
                    entry.Value.Job?.Queue ?? entry.Value.InvocationData?.Queue, new DateTimeOffset(DateTime.SpecifyKind(entry.Value.EnqueueAt, DateTimeKind.Utc)))));
            if (page.Count < PageSize)
            {
                break;
            }
        }

        for (var from = 0; includeFailed; from += PageSize)
        {
            var page = monitoring.FailedJobs(from, PageSize);
            items.AddRange(page.Where(entry => entry.Value.InFailedState).Select(entry =>
                Job(HangfireJobKind.Failed, entry.Key, entry.Value.Job, entry.Value.InvocationData, entry.Value.LoadException,
                    entry.Value.Job?.Queue ?? entry.Value.InvocationData?.Queue, null)));
            if (page.Count < PageSize)
            {
                break;
            }
        }

        DuplicateSchedules(items);
        items = Chains(items, continuations, storage);

        return new(items, monitoring.Servers().Count, monitoring.ProcessingCount(), monitoring.FailedCount());
    }

    /// <summary>A continuation waiting in Hangfire: its parent, and whether it runs after any finish rather than only success.</summary>
    private HangfireMigrationItem Continuation(IStorageConnection connection, string id)
    {
        var data = connection.GetJobData(id);
        IDictionary<string, string> state = connection.GetStateData(id)?.Data ?? new Dictionary<string, string>();
        var queue = state.TryGetValue("NextState", out var next) && next is { Length: > 0 }
            && JsonDocument.Parse(next).RootElement.TryGetProperty("Queue", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;
        var item = Convert(HangfireJobKind.Continuation, id, data?.Job, data?.InvocationData, data?.LoadException, data?.Job?.Queue ?? queue)
            with
        { ParentId = state.TryGetValue("ParentId", out var parent) ? parent : null };
        if (item.ParentId is null)
        {
            return item with { Problem = "Hangfire stores no parent for this continuation." };
        }

        return state.TryGetValue("Options", out var options) && options != nameof(JobContinuationOptions.OnlyOnSucceededState) && item.IsReady
            ? item with { Problem = $"The continuation runs on {options}; a flow step runs only after its parent succeeds." }
            : item;
    }

    /// <summary>
    /// Attaches each continuation to the job at the root of its chain. A chain moves whole: one problem in it keeps every job of the
    /// chain in Hangfire. A continuation whose root is not being imported (running, finished, or a failed job without IncludeFailed)
    /// stays too.
    /// </summary>
    private static List<HangfireMigrationItem> Chains(List<HangfireMigrationItem> items, List<HangfireMigrationItem> continuations,
        JobStorage storage)
    {
        if (continuations.Count == 0)
        {
            return items;
        }

        var byId = continuations.ToDictionary(item => item.HangfireId, StringComparer.Ordinal);
        var roots = items.Where(item => item.Kind is not HangfireJobKind.Recurring).ToDictionary(item => item.HangfireId, StringComparer.Ordinal);
        string? RootOf(HangfireMigrationItem continuation)
        {
            var parent = continuation.ParentId;
            for (var depth = 0; parent is not null && depth <= byId.Count; depth++)
            {
                if (roots.ContainsKey(parent))
                {
                    return parent;
                }

                parent = byId.GetValueOrDefault(parent)?.ParentId;
            }

            return null;
        }

        var chains = continuations.GroupBy(RootOf).ToList();
        var result = items.ToList();
        using var connection = storage.GetConnection();
        foreach (var chain in chains)
        {
            if (chain.Key is null)
            {
                result.AddRange(chain.Select(item => item.IsReady
                    ? item with { Problem = $"Its parent {item.ParentId} is {connection.GetStateData(item.ParentId!)?.Name ?? "gone"} in Hangfire, not being imported." }
                    : item));

                continue;
            }

            var root = roots[chain.Key];
            var members = chain.ToList();
            var blocked = !root.IsReady ? $"job {root.HangfireId}: {root.Problem}"
                : members.FirstOrDefault(member => !member.IsReady) is { } failing ? $"job {failing.HangfireId}: {failing.Problem}" : null;
            var index = result.IndexOf(root);
            result[index] = blocked is null ? root with { Continuations = members } : root with { Problem = $"Its continuation chain cannot move ({blocked})." };
            if (blocked is not null)
            {
                result.AddRange(members.Select(member => member.IsReady ? member with { Problem = $"Its chain cannot move ({blocked})." } : member));
            }
        }

        return result;
    }

    private HangfireMigrationItem Job(HangfireJobKind kind, string id, Job? job, InvocationData? stored, Exception? loadException, string? queue,
        DateTimeOffset? notBefore)
    {
        var item = Convert(kind, id, job, stored, loadException, queue);

        return item.IsReady ? item with { NotBefore = notBefore } : item;
    }

    private HangfireMigrationItem Recurring(string id, Job? job, InvocationData? stored, Exception? loadException, string? cron, string? timeZone,
        string? queue)
    {
        var item = Convert(HangfireJobKind.Recurring, id, job, stored, loadException, queue);
        if (!item.IsReady)
        {
            return item;
        }

        // A seconds field of 0 adds nothing, and five fields need no newer engine protocol.
        var fields = (cron ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = scheduleKey(id);
        var schedule = item with
        {
            ScheduleKey = key,
            Cron = fields is ["0", _, _, _, _, _] ? string.Join(' ', fields.Skip(1)) : cron,
            TimeZone = string.IsNullOrEmpty(timeZone) ? ScheduleDefinition.DefaultTimeZone : timeZone
        };
        try
        {
            ScheduleClock.Validate(Definition(schedule));
        }
        catch (ArgumentException error)
        {
            return item with { Problem = $"Not a valid WorkThrough schedule: {error.Message}" };
        }

        return schedule;
    }

    private HangfireMigrationItem Convert(HangfireJobKind kind, string id, Job? job, InvocationData? stored, Exception? loadException, string? queue)
    {
        var typeName = job?.Type.FullName ?? stored?.Type?.Split(',')[0].Trim();
        var methodName = job?.Method.Name ?? stored?.Method;
        var method = $"{typeName ?? "?"}.{methodName ?? "?"}";
        try
        {
            if (job is not null && mappings.FirstOrDefault(candidate => Matches(candidate, job)) is { } typed)
            {
                return new(kind, id, method)
                {
                    Target = typed.Target,
                    Input = typed.Convert(new(job.Type, job.Method, job.Args)),
                    Queue = typed.Queue ?? queue ?? JobDefaults.Queue
                };
            }

            if (stored is not null && nameMappings.FirstOrDefault(candidate => candidate.Type == typeName && candidate.Method == methodName) is { } named)
            {
                return new(kind, id, method)
                {
                    Target = named.Target,
                    Input = named.Convert(new(typeName!, methodName!, StoredArguments(stored))),
                    Queue = named.Queue ?? queue ?? JobDefaults.Queue
                };
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidCastException or JsonException)
        {
            return new(kind, id, method) { Problem = $"The mapping could not convert the arguments: {error.Message}" };
        }

        return new(kind, id, method)
        {
            Problem = job is null && stored is null ? $"Hangfire could not load the job: {loadException?.Message ?? "unknown type"}" : "No mapping for this method."
        };
    }

    /// <summary>
    /// Each stored argument as JSON. Hangfire stores an argument as a JSON string holding the argument's own JSON, which is unwrapped.
    /// </summary>
    private static IReadOnlyList<JsonElement> StoredArguments(InvocationData stored)
    {
        if (string.IsNullOrEmpty(stored.Arguments))
        {
            return [];
        }

        using var document = JsonDocument.Parse(stored.Arguments);

        return [.. document.RootElement.EnumerateArray().Select(argument =>
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                return argument.Clone();
            }

            try
            {
                using var inner = JsonDocument.Parse(argument.GetString()!);

                return inner.RootElement.Clone();
            }
            catch (JsonException)
            {
                return argument.Clone();
            }
        })];
    }

    /// <summary>The mapped method itself, or the same method on an implementation of the mapped service.</summary>
    private static bool Matches(Mapping mapping, Job job) => job.Method == mapping.Method
        || mapping.Service.IsAssignableFrom(job.Type) && job.Method.Name == mapping.Method.Name
            && job.Method.GetParameters().Select(p => p.ParameterType).SequenceEqual(mapping.Method.GetParameters().Select(p => p.ParameterType));

    private static void DuplicateSchedules(List<HangfireMigrationItem> items)
    {
        var duplicates = items.Where(item => item.ScheduleKey is not null).GroupBy(item => item.ScheduleKey).Where(group => group.Count() > 1)
            .SelectMany(group => group).ToHashSet();
        for (var index = 0; index < items.Count; index++)
        {
            if (duplicates.Contains(items[index]))
            {
                items[index] = items[index] with { Problem = $"Schedule key {items[index].ScheduleKey} is shared with another recurring job; use NameSchedules." };
            }
        }
    }

    private static ScheduleDefinition Definition(HangfireMigrationItem item) =>
        new(item.ScheduleKey!, 1, item.Cron!, item.TimeZone!, item.Target!, item.Input!.Value, item.Queue);

    private static string DefaultScheduleKey(string id)
    {
        var key = SchedulePrefix + InvalidKeyCharacters().Replace(id.ToLowerInvariant(), "-");

        return key.Length <= 120 ? key : key[..120];
    }

    [GeneratedRegex("[^a-z0-9.-]")]
    private static partial Regex InvalidKeyCharacters();

    /// <summary>
    /// Imports every ready item: jobs keep their queue and due time under the idempotency key <c>hangfire:&lt;id&gt;</c>, and recurring
    /// jobs become published, enabled schedules. Running it again imports nothing twice. Refuses while Hangfire servers run, unless
    /// <paramref name="allowRunningServers"/>, because those servers could run the same jobs.
    /// </summary>
    public async Task<HangfireMigrationResult> ImportAsync(HangfireMigrationPlan plan, IJobStore store, IJobSchedules schedules,
        bool allowRunningServers = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schedules);
        if (plan.RunningServers > 0 && !allowRunningServers)
        {
            throw new InvalidOperationException($"{plan.RunningServers} Hangfire servers are running and could run the same jobs. Stop them, then plan again.");
        }

        var client = new JobClient(store);
        var existing = (await schedules.SchedulesAsync(cancellationToken)).Select(schedule => schedule.Definition.Key).ToHashSet(StringComparer.Ordinal);
        var imported = new List<HangfireImported>();
        var skipped = plan.Problems.ToList();
        foreach (var item in plan.Ready)
        {
            if (item.Continuations is { Count: > 0 } chain)
            {
                var run = await new WorkflowClient(store).StartAsync(ChainFlow(item, chain), new { },
                    new FlowStart(IdempotencyKey: IdempotencyPrefix + item.HangfireId) { NotBefore = item.NotBefore }, cancellationToken);
                imported.Add(new(item, null) { RunId = run });
                imported.AddRange(chain.Select(continuation => new HangfireImported(continuation, null) { RunId = run }));

                continue;
            }

            if (item.Kind != HangfireJobKind.Recurring)
            {
                var id = await client.EnqueueAsync(new JobDefinition<JsonElement>(item.Target!), item.Input!.Value,
                    new(Queue: item.Queue, NotBefore: item.NotBefore, IdempotencyKey: IdempotencyPrefix + item.HangfireId), cancellationToken);
                imported.Add(new(item, id));

                continue;
            }

            var problem = existing.Contains(item.ScheduleKey!) ? null : await PublishAsync(schedules, item, cancellationToken);
            if (problem is null)
            {
                imported.Add(new(item, null));
            }
            else
            {
                skipped.Add(item with { Problem = problem });
            }
        }

        return new(imported, skipped);
    }

    /// <summary>A job and its continuations as one flow: each continuation is a step that runs after its parent succeeds.</summary>
    private static FlowDefinition ChainFlow(HangfireMigrationItem root, IReadOnlyList<HangfireMigrationItem> chain)
    {
        var flow = new FlowBuilder("hangfire.chain." + InvalidKeyCharacters().Replace(root.HangfireId.ToLowerInvariant(), "-"), 1);
        foreach (var job in chain.Prepend(root))
        {
            flow.Step(StepId(job.HangfireId), job.Target!, job.Input!.Value, job.Queue);
            if (job.ParentId is { } parent)
            {
                flow.After(StepId(parent));
            }
        }

        return flow.Build();
    }

    private static string StepId(string hangfireId) => "job-" + hangfireId;

    /// <summary>Publishes and enables a schedule; returns why it failed, or null. Retries deduplicate on the job's ID.</summary>
    private static async Task<string?> PublishAsync(IJobSchedules schedules, HangfireMigrationItem item, CancellationToken ct)
    {
        var created = await schedules.CreatePublishedAsync(Definition(item), enable: true, Actor, IdempotencyPrefix + item.HangfireId,
            $"Imported from Hangfire recurring job {item.HangfireId}", ct);

        return created.IsSuccess ? null : created.Error!.Message;
    }

    /// <summary>
    /// Deletes the imported jobs in Hangfire and removes its imported recurring jobs, so restarting Hangfire cannot run them again.
    /// Call it after checking the import; jobs that were skipped stay in Hangfire.
    /// </summary>
    public void Retire(HangfireMigrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var jobs = new BackgroundJobClient(storage);
        var recurring = new RecurringJobManager(storage);
        // Continuations go before their parents, so deleting a parent cannot release a continuation in Hangfire.
        foreach (var item in result.Imported.Select(imported => imported.Item).OrderBy(item => item.ParentId is null))
        {
            if (item.Kind == HangfireJobKind.Recurring)
            {
                recurring.RemoveIfExists(item.HangfireId);
            }
            else
            {
                jobs.Delete(item.HangfireId);
            }
        }
    }
}
