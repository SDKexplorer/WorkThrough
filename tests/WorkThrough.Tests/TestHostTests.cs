using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

[JobContract("tests.host.greet")]
public sealed record Greet(string Name);

public sealed record Greeting(string Text);

public sealed class GreetJob(TestHostTests.Attempts attempts) : IJob<Greet>
{
    public Task<JobResult> ExecuteAsync(Greet input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // Fails the first attempt of names marked flaky, so retries and their backoff can be exercised.
        if (input.Name.StartsWith("flaky", StringComparison.Ordinal) && attempts.Count(input.Name) == 1)
        {
            throw new InvalidOperationException("transient");
        }

        return input.Name == "never" ? throw new InvalidOperationException("always fails") : Task.FromResult(JobResult.From(new Greeting($"Hello {input.Name}")));
    }
}

/// <summary>The public test host: real store and worker, a movable clock, waits and flow assertions.</summary>
public sealed class TestHostTests
{
    public sealed class Attempts
    {
        private readonly Dictionary<string, int> counts = [];

        public int Count(string name)
        {
            lock (counts)
            {
                return counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
    }

    private static Task<WorkThroughTestHost> StartAsync() =>
        WorkThroughTestHost.StartAsync(workthrough => workthrough.AddJob<Greet, GreetJob>(), services => services.AddSingleton<Attempts>());

    /// <summary>Retries an hour later, so a retry falls due only when the test moves the clock, however slow the machine is.</summary>
    private sealed class HourlyRetry : IJobRetryPolicy
    {
        public RetryDecision Decide(JobRecord job, Exception exception) => RetryDecision.After(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Advancing_the_clock_runs_delayed_work_and_retries_without_waiting()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddJob<Greet, GreetJob>().UseRetryPolicy<HourlyRetry>(),
            services => services.AddSingleton<Attempts>());
        var later = await host.Jobs.EnqueueAsync(new Greet("later"), new(NotBefore: host.UtcNow.AddHours(1)));
        var flaky = await host.Jobs.EnqueueAsync(new Greet("flaky"));

        await host.WaitUntilIdleAsync();
        Assert.Equal(JobStatus.Queued, (await host.Queries.GetAsync(later))!.Status);
        Assert.Equal(JobStatus.Queued, (await host.Queries.GetAsync(flaky))!.Status);

        await host.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(JobStatus.Succeeded, (await host.WaitForJobAsync(later)).Status);
        var retried = await host.WaitForJobAsync(flaky);
        Assert.Equal((JobStatus.Succeeded, 2), (retried.Status, retried.Attempt));
    }

    [Fact]
    public async Task A_flow_started_for_later_waits_for_its_time()
    {
        await using var host = await StartAsync();
        var flow = new FlowBuilder("tests.host.later", 1)
            .Step<Greet>("first", new Greet("ada"))
            .Step<Greet>("second", new Greet("grace")).After("first")
            .Build();
        var run = await host.Flows.StartAsync(flow, new { }, new FlowStart { NotBefore = host.UtcNow.AddHours(1) });

        await host.WaitUntilIdleAsync();
        Assert.All(await host.Queries.ListFlowJobsAsync(run), job => Assert.NotEqual(JobStatus.Succeeded, job.Status));
        await host.AdvanceAsync(TimeSpan.FromHours(1));

        (await host.WaitForFlowAsync(run)).AssertSucceeded();
    }

    [Fact]
    public async Task Flow_results_expose_typed_outputs_and_describe_failures()
    {
        await using var host = await StartAsync();
        var flow = new FlowBuilder("tests.host.flow", 1).OnFailure(FlowFailurePolicy.Stop)
            .Step<Greet>("first", new Greet("ada"))
            .Step<Greet>("second", new Greet("grace")).After("first")
            .Build();

        var succeeded = (await host.WaitForFlowAsync(await host.Flows.StartAsync(flow, new { }))).AssertSucceeded();
        Assert.Equal("Hello grace", succeeded.Step("second").Output<Greeting>().Text);

        var failing = new FlowBuilder("tests.host.failing", 1).OnFailure(FlowFailurePolicy.Stop).Step<Greet>("only", new Greet("never"), maxAttempts: 1).Build();
        var failed = await host.WaitForFlowAsync(await host.Flows.StartAsync(failing, new { }));
        var error = Assert.Throws<WorkThroughAssertionException>(() => failed.AssertSucceeded());
        Assert.Contains("only Failed", error.Message);
    }
}
