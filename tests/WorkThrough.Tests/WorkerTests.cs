using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task Worker_applies_local_capacity_and_resolves_each_handler_in_its_own_scope()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe();
        using var host = BuildHost(harness.ConnectionString, probe, maximum: 2);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 6; i++)
        {
            await client.EnqueueAsync(new Alpha(i.ToString()));
        }

        await host.StartAsync();
        try
        {
            await EventuallyAsync(() => Task.FromResult(probe.Active == 2));
            Assert.Equal(2, probe.Maximum);
            probe.Gate.Release(6);
            await EventuallyAsync(async () => (await harness.Store.ListAsync(new())).All(x => x.Status == JobStatus.Succeeded));
            Assert.Equal(6, probe.ScopeIds.Distinct().Count());
            Assert.Equal(2, probe.Maximum);
        }
        finally
        {
            probe.Gate.Release(20);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_shutdown_requeues_cooperatively_interrupted_work()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe();
        using var host = BuildHost(harness.ConnectionString, probe, maximum: 1);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("long"), new(MaxAttempts: 1));
        await host.StartAsync();
        await EventuallyAsync(() => Task.FromResult(probe.Active == 1));
        await host.StopAsync();
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(id))!.Status);
        Assert.Equal(AttemptStatus.Interrupted, (await harness.Store.AttemptsAsync(id)).Single().Status);
    }

    [Fact]
    public async Task Processor_registry_change_stops_acquisition_and_the_host()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe();
        using var host = BuildHost(harness.ConnectionString, probe, maximum: 1);
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var alias = new ProcessorAliasMapping(new("tests.legacy-alpha", 1), JobContractId.For<Alpha>());
        await harness.Store.SetProcessorAliasesAsync([alias], 0, new(Guid.NewGuid(), new("tests", "operator"), "Activate reviewed alias")).SucceedsAsync();
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("after-change"));
        await EventuallyAsync(() => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested));
        await host.StopAsync();
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(id))!.Status);
        Assert.Equal(0, probe.Maximum);
    }

    [Fact]
    public async Task Worker_refuses_to_start_when_a_handler_dependency_is_missing()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddWorkThrough().UseSqlite(harness.ConnectionString).AddJob<Alpha, NeedsDependency>().AddWorker();
        using var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Replacement_attempt_is_tracked_while_revoked_handler_still_runs()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe { IgnoreCancellation = true };
        using var host = BuildHost(harness.ConnectionString, probe, maximum: 2);
        var client = new JobClient(harness.Store);
        var id = await client.EnqueueAsync(new Alpha("replaced"));
        await host.StartAsync();
        try
        {
            await EventuallyAsync(() => Task.FromResult(probe.Active == 1));
            Assert.True(await harness.Store.CancelAsync(id));
            Assert.True(await harness.Store.RetryAsync(id));
            await EventuallyAsync(() => Task.FromResult(probe.Active == 2));
            probe.Gate.Release(2);
            await EventuallyAsync(async () => (await harness.Store.GetAsync(id))!.Status == JobStatus.Succeeded);
            // The acquisition loop must remain alive, and must not lose track of the stale physical task.
            var next = await client.EnqueueAsync(new Alpha("next"));
            probe.Gate.Release();
            await EventuallyAsync(async () => (await harness.Store.GetAsync(next))!.Status == JobStatus.Succeeded);
            Assert.Equal(2, probe.Maximum);
            Assert.Equal([AttemptStatus.Cancelled, AttemptStatus.Succeeded], (await harness.Store.AttemptsAsync(id)).Select(x => x.Status));
        }
        finally
        {
            probe.Gate.Release(20);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Shutdown_during_attempt_setup_releases_ownership_without_spending_retry_budget()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe();
        var gatedStore = new SetupGateStore(harness.Store);
        using var host = BuildHost(harness.ConnectionString, probe, maximum: 1, gatedStore);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("setup"), new(MaxAttempts: 1));
        await host.StartAsync();
        try
        {
            await gatedStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(0, probe.Active);
        Assert.Empty(probe.ScopeIds);
        var job = (await harness.Store.GetAsync(id))!;
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(2, job.MaxAttempts);
        Assert.Equal(AttemptStatus.Interrupted, (await harness.Store.AttemptsAsync(id)).Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_deadline_records_failure_and_keeps_noncooperative_work_visible(bool ignoresCancellation)
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var probe = new Probe { IgnoreCancellation = ignoresCancellation };
        using var host = BuildHost(harness.ConnectionString, probe, 1, timeout: TimeSpan.FromMilliseconds(120));
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("timeout"), new(MaxAttempts: 1));
        await host.StartAsync();
        try
        {
            if (ignoresCancellation)
            {
                await EventuallyAsync(async () => (await harness.Store.HealthAsync(id))?.SuspectedStalled == true);
                Assert.Equal(JobStatus.Running, (await harness.Store.GetAsync(id))!.Status);
                Assert.Equal(1, probe.Active);
                probe.Gate.Release();
            }

            await EventuallyAsync(async () => (await harness.Store.GetAsync(id))!.Status == JobStatus.Failed);
            Assert.Equal("System.TimeoutException", (await harness.Store.FailuresAsync(id)).Single().Failure.ExceptionType);
            Assert.Equal(AttemptStatus.Failed, (await harness.Store.AttemptsAsync(id)).Single().Status);
        }
        finally
        {
            probe.Gate.Release(10);
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(string connection, Probe probe, int maximum, IJobStore? store = null, TimeSpan? timeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(probe);
        builder.Services.AddScoped<ScopeMarker>();
        builder.Services.AddWorkThrough().UseSqlite(connection).AddJob<Alpha, BlockingHandler>()
            .AddWorker(options =>
            {
                options.MaxConcurrentJobs = maximum;
                options.PollInterval = TimeSpan.FromMilliseconds(20);
                options.LeaseDuration = TimeSpan.FromSeconds(3);
                options.ExecutionTimeout = timeout;
            });
        if (store is not null)
        {
            builder.Services.AddSingleton(store);
        }

        return builder.Build();
    }

    internal static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    public sealed class Probe
    {
        public readonly SemaphoreSlim Gate = new(0);
        public readonly System.Collections.Concurrent.ConcurrentBag<Guid> ScopeIds = [];
        public int Active;
        public int Maximum;
        public bool IgnoreCancellation;
        public void Enter(Guid scope)
        {
            ScopeIds.Add(scope);
            var active = Interlocked.Increment(ref Active);
            int observed;
            do
            {
                observed = Volatile.Read(ref Maximum);
            } while (active > observed && Interlocked.CompareExchange(ref Maximum, active, observed) != observed);
        }
    }
    public sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
    public sealed class BlockingHandler(Probe probe, ScopeMarker marker) : IJobHandler<Alpha>
    {
        public async Task<JobResult> HandleAsync(Alpha job, JobExecutionContext context, CancellationToken cancellationToken)
        {
            probe.Enter(marker.Id);
            try
            {
                await probe.Gate.WaitAsync(probe.IgnoreCancellation ? CancellationToken.None : cancellationToken);

                return JobResult.Done();
            }
            finally
            {
                Interlocked.Decrement(ref probe.Active);
            }
        }
    }
    public interface IMissingDependency;
    public sealed class NeedsDependency(IMissingDependency dependency) : IJobHandler<Alpha>
    {
        public Task<JobResult> HandleAsync(Alpha job, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.From(dependency.ToString()));
    }

    private sealed class SetupGateStore(IJobStore inner) : IJobStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RecordHandlerAsync(Guid id, Guid token, string handler, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<Guid> EnqueueAsync(JobRecord job, CancellationToken ct = default) => inner.EnqueueAsync(job, ct);
        public Task SeedLimitsAsync(IReadOnlyList<ConcurrencyLimit> limits, CancellationToken ct = default) => inner.SeedLimitsAsync(limits, ct);
        public Task SetLimitAsync(ConcurrencyLimit limit, CancellationToken ct = default) => inner.SetLimitAsync(limit, ct);
        public Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken ct = default) => inner.ClaimAsync(request, ct);
        public Task<bool> RenewAsync(Guid id, Guid token, TimeSpan duration, CancellationToken ct = default) => inner.RenewAsync(id, token, duration, ct);
        public Task<bool> CompleteAsync(Guid id, Guid token, JobResult result, CancellationToken ct = default) => inner.CompleteAsync(id, token, result, ct);
        public Task<bool> FailAsync(Guid id, Guid token, string error, TimeSpan delay, CancellationToken ct = default) => inner.FailAsync(id, token, error, delay, ct);
        public Task<bool> ReleaseAsync(Guid id, Guid token, string reason, CancellationToken ct = default) => inner.ReleaseAsync(id, token, reason, ct);
        public Task<bool> RetryAsync(Guid id, CancellationToken ct = default) => inner.RetryAsync(id, ct);
        public Task<bool> CancelAsync(Guid id, CancellationToken ct = default) => inner.CancelAsync(id, ct);
        public Task<Guid> StartWorkflowAsync(FlowDefinition definition, System.Text.Json.JsonElement input, string? correlation, CancellationToken ct = default) => inner.StartWorkflowAsync(definition, input, correlation, ct);
    }
}
