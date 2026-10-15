using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

[JobContract("tests.delegate.greet")]
public sealed record Hello(string Name);

/// <summary>Jobs registered as delegates at an explicit address.</summary>
public sealed class DelegateJobTests
{
    private static readonly JobDefinition<Hello> Greeting = new("tests.delegate.greeting");

    public sealed class Greeter
    {
        public string Greet(string name) => $"Hello {name}";
    }

    [Fact]
    public async Task A_delegate_job_runs_with_its_scoped_services_and_returns_output()
    {
        var seen = new List<string>();
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .AddJob(Greeting, async job =>
            {
                await Task.Yield();

                return JobResult.From(job.Service<Greeter>().Greet(job.Input.Name));
            })
            .AddJob<Hello>(job =>
            {
                seen.Add($"{job.Input.Name}:{job.Context.Attempt}");

                return Task.CompletedTask;
            }), services => services.AddSingleton<Greeter>());

        var greeted = await host.WaitForJobAsync(await host.Jobs.EnqueueAsync(Greeting, new Hello("Ada")));
        var plain = await host.WaitForJobAsync(await host.Jobs.EnqueueAsync(new Hello("Grace")));

        Assert.Equal("Hello Ada", greeted.Output!.Value.GetString());
        Assert.Equal(JobStatus.Succeeded, plain.Status);
        Assert.Equal(["Grace:1"], seen);
    }

    [Fact]
    public void An_address_takes_one_delegate()
    {
        var catalog = new JobCatalog();
        catalog.Register(Greeting, _ => Task.FromResult(JobResult.Done()));

        Assert.Throws<InvalidOperationException>(() => catalog.Register(Greeting, _ => Task.FromResult(JobResult.Done())));
    }
}
