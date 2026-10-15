using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ObservabilityTests
{
    private const string ProducerSource = "tests.producer";

    [Fact]
    public async Task An_attempt_continues_the_trace_that_enqueued_it()
    {
        await using var harness = await StoreHarness.CreateAsync("postgres");
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is WorkThroughTelemetry.Name or ProducerSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => attempts.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using var producer = new ActivitySource(ProducerSource);
        Guid id;
        ActivityTraceId trace;
        using (var request = producer.StartActivity("request")!)
        {
            trace = request.TraceId;
            id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("traced"));
        }

        Assert.NotNull((await harness.Store.GetAsync(id))!.TraceParent);
        using var host = Host(harness, harness.Store);
        await host.StartAsync();
        try
        {
            await WorkerTests.EventuallyAsync(() => Task.FromResult(attempts.Any(activity => Equals(activity.GetTagItem("workthrough.job.id"), id))));
            Assert.Equal(trace, attempts.First(activity => Equals(activity.GetTagItem("workthrough.job.id"), id)).TraceId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Health_checks_report_storage_and_failing_acquisition()
    {
        await using var harness = await StoreHarness.CreateAsync("postgres");
        using (var healthy = Host(harness, harness.Store))
        {
            await healthy.StartAsync();
            var report = await healthy.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.Equal(HealthStatus.Healthy, report.Status);
            await healthy.StopAsync();
        }

        using var failing = Host(harness, new UnreachableClaims(harness.Store));
        await failing.StartAsync();
        try
        {
            var health = failing.Services.GetRequiredService<HealthCheckService>();
            await WorkerTests.EventuallyAsync(async () =>
                (await health.CheckHealthAsync()).Entries[WorkThroughHealthChecks.Worker].Status == HealthStatus.Unhealthy);
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Entries[WorkThroughHealthChecks.Storage].Status);
        }
        finally
        {
            await failing.StopAsync();
        }
    }

    private static IHost Host(StoreHarness harness, IJobStore store)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddWorkThrough().UsePostgreSql(harness.ConnectionString).AddJob<Alpha, EchoHandler>()
            .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
        builder.Services.AddSingleton(store);
        builder.Services.AddHealthChecks().AddWorkThrough();

        return builder.Build();
    }

    public sealed class EchoHandler : IJob<Alpha>
    {
        public Task<JobResult> ExecuteAsync(Alpha job, JobExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(JobResult.From(job.Value));
    }

    private sealed class UnreachableClaims(IJobStore inner) : DelegatingJobStore(inner)
    {
        public override Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken ct = default) =>
            throw new TimeoutException("Simulated unreachable store");
    }
}
