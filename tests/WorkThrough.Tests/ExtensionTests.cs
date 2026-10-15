using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ExtensionTests
{
    [Theory]
    [InlineData("valid", JobStatus.Succeeded)]
    [InlineData("invalid", JobStatus.Rejected)]
    [InlineData("before", JobStatus.Failed)]
    [InlineData("after", JobStatus.Failed)]
    [InlineData("twice", JobStatus.Failed)]
    [InlineData("early", JobStatus.Failed)]
    public async Task Scoped_pipeline_validates_orders_and_rejects_invalid_continuation_usage(string behavior, JobStatus expected)
    {
        await using var database = await StoreHarness.CreateAsync("postgres");
        var probe = new PipelineProbe(behavior);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(probe);
        builder.Services.AddScoped<Lifetime>();
        builder.Services.AddWorkThrough().UsePostgreSql(database.ConnectionString)
            .AddJob<Alpha, Processor>(new JobDefinition<Alpha>("tests.pipeline"))
            .AddMiddleware<Outer>().AddValidator<Alpha, Validator>().AddMiddleware<Inner>()
            .AddObserver<BrokenObserver>().AddObserver<CommittedObserver>()
            .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(10));
        using var host = builder.Build();
        var id = await new JobClient(database.Store).EnqueueAsync(new JobDefinition<Alpha>("tests.pipeline"), new Alpha(behavior), new(MaxAttempts: 1));
        await host.StartAsync();
        try
        {
            await WorkerTests.EventuallyAsync(async () => (await database.Store.GetAsync(id))?.Status == expected && probe.Observed);
            Assert.Equal(expected, (await database.Store.GetAsync(id))!.Status);
            Assert.Single(await database.Store.AttemptsAsync(id));
            Assert.False(probe.DisposedWhileRunning);
            if (behavior == "valid")
            {
                Assert.Equal(["outer.before", "validate", "inner.before", "processor", "inner.after", "outer.after", "committed"], probe.Trace);
            }

            if (behavior is "invalid" or "before")
            {
                Assert.DoesNotContain("processor", probe.Trace);
            }

            Assert.Equal(1, probe.Trace.Count(x => x == "committed"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void Typed_field_selector_resolves_serialized_property_and_rejects_computation()
    {
        Assert.Equal("wire_value", JsonField.Of<CustomField, string>(x => x.Value));
        Assert.Throws<ArgumentException>(() => JsonField.Of<Alpha, int>(x => x.Value.Length));
        Assert.Throws<ArgumentException>(() => JsonField.Of<Alpha, string>(x => x.Value.ToUpperInvariant()));
    }

    public sealed record CustomField([property: System.Text.Json.Serialization.JsonPropertyName("wire_value")] string Value);
    public sealed class PipelineProbe(string behavior)
    {
        public string Behavior { get; } = behavior;
        public ConcurrentQueue<string> Trace { get; } = new();
        public volatile bool Observed;
        public bool DisposedWhileRunning;
    }
    public sealed class Lifetime : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class Processor(PipelineProbe probe, Lifetime lifetime) : IJob<Alpha>
    {
        public async Task<JobResult> ExecuteAsync(Alpha input, JobExecutionContext context, CancellationToken ct)
        {
            probe.Trace.Enqueue("processor");
            await Task.Delay(50, ct);
            probe.DisposedWhileRunning |= lifetime.Disposed;

            return JobResult.Done();
        }
    }
    public sealed class Validator(PipelineProbe probe) : IJobValidator<Alpha>
    {
        public Task ValidateAsync(Alpha input, JobExecutionContext context, CancellationToken ct)
        {
            probe.Trace.Enqueue("validate");
            if (input.Value == "invalid")
            {
                throw new JobRejectedException("Invalid input");
            }

            return Task.CompletedTask;
        }
    }
    public sealed class Outer(PipelineProbe probe) : IJobMiddleware
    {
        public async Task<JobResult> InvokeAsync(JobInvocation invocation, JobContinuation next, CancellationToken ct)
        {
            probe.Trace.Enqueue("outer.before");
            var result = await next();
            probe.Trace.Enqueue("outer.after");

            return result;
        }
    }
    public sealed class Inner(PipelineProbe probe) : IJobMiddleware
    {
        public async Task<JobResult> InvokeAsync(JobInvocation invocation, JobContinuation next, CancellationToken ct)
        {
            probe.Trace.Enqueue("inner.before");

            if (probe.Behavior == "before")
            {
                throw new InvalidOperationException("Before invocation");
            }

            if (probe.Behavior == "early")
            {
                _ = next();

                return JobResult.Done();
            }

            var result = await next();
            if (probe.Behavior == "twice")
            {
                await next();
            }

            if (probe.Behavior == "after")
            {
                throw new InvalidOperationException("After invocation");
            }

            probe.Trace.Enqueue("inner.after");

            return result;
        }
    }
    public sealed class BrokenObserver : IJobObserver
    {
        public Task OnSettledAsync(AttemptSettlement settlement, CancellationToken ct) => throw new InvalidOperationException("Observer failure");
    }
    public sealed class CommittedObserver(PipelineProbe probe, IJobQueries queries) : IJobObserver
    {
        public async Task OnSettledAsync(AttemptSettlement settlement, CancellationToken ct)
        {
            var job = await queries.GetAsync(settlement.JobId, ct);
            Assert.NotEqual(JobStatus.Running, job!.Status);
            probe.Trace.Enqueue("committed");
            probe.Observed = true;
        }
    }
}
