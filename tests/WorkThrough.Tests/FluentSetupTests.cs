using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public sealed record Reminder(string Customer);

/// <summary>Setting up a job in one place, and enqueueing it fluently.</summary>
public sealed class FluentSetupTests
{
    private const string Queue = "reminders";
    private static readonly JobDefinition<Reminder> Remind = new JobDefinition<Reminder>("tests.fluent.remind").InQueue(Queue).WithMaxAttempts(2);

    [Fact]
    public async Task One_setup_registers_the_job_its_schedule_and_its_limit_and_producers_inherit_its_defaults()
    {
        var reminded = new List<string>();
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Job(Remind, job => job
                .Run(call =>
                {
                    lock (reminded)
                    {
                        reminded.Add(call.Input.Customer);
                    }

                    return Task.CompletedTask;
                })
                .Every("0 9 * * *", new Reminder("everyone"), "Europe/Bucharest")
                .AtMostRunning(3)), queues: [Queue]);

        var first = await host.Jobs.Enqueue(Remind, new Reminder("ada")).Idempotent("remind:ada").ForTenant("acme");
        var again = await host.Jobs.Enqueue(Remind, new Reminder("ada")).Idempotent("remind:ada").ForTenant("acme");
        var later = await host.Jobs.Enqueue(Remind, new Reminder("grace")).After(TimeSpan.FromHours(1));

        var job = await host.WaitForJobAsync(first);
        Assert.Equal(first, again);
        Assert.Equal((Queue, 2, "acme"), (job.Queue, job.MaxAttempts, job.TenantId));
        Assert.Equal(JobStatus.Queued, (await host.Queries.GetAsync(later))!.Status);
        await host.AdvanceAsync(TimeSpan.FromHours(2));
        await host.WaitForJobAsync(later);
        Assert.Equal(["ada", "grace"], reminded.Order());

        var schedule = Assert.Single(await host.Store.SchedulesAsync());
        Assert.Equal((Remind.Identity.Key, "0 9 * * *", "Europe/Bucharest", Queue, 2),
            (schedule.Definition.Key, schedule.Definition.Cron, schedule.Definition.TimeZone, schedule.Definition.Queue, schedule.Definition.MaxAttempts));
        Assert.Contains((await host.Store.StatisticsAsync()).Limits, limit => limit.JobKey == Remind.Identity.Key && limit.Maximum == 3);
    }

    [Fact]
    public async Task A_job_takes_one_handler()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Job(Remind, job => job.Run(_ => Task.CompletedTask).Run(_ => Task.CompletedTask))));

        Assert.Contains("already has a job", error.Message);
    }
}
