using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using WorkThrough.Migration.Hangfire;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public class LegacyMailer
{
    public void SendWelcome(string address) => throw new NotSupportedException(address);
}

public class LegacyReports
{
    public Task Daily(int days) => Task.FromException(new NotSupportedException(days.ToString()));

    public void Unmapped()
    {
    }
}

public sealed record WelcomeEmail(string Address);
public sealed record DailyReport(int Days);

/// <summary>Hangfire's recurring, enqueued and scheduled jobs moving into WorkThrough.</summary>
public sealed class HangfireMigrationTests
{
    private static readonly JobDefinition<WelcomeEmail> Welcome = new("tests.hangfire.welcome");
    private static readonly JobDefinition<DailyReport> Report = new("tests.hangfire.report");

    private static HangfireMigration Migration(JobStorage storage) => new HangfireMigration(storage)
        .Map((LegacyMailer mailer) => mailer.SendWelcome(default!), Welcome, call => new WelcomeEmail(call.Argument<string>(0)))
        .Map((LegacyReports reports) => reports.Daily(default), Report, call => new DailyReport(call.Argument<int>(0)));

    [Fact]
    public async Task Mapped_jobs_and_schedules_move_once_and_are_retired_in_hangfire()
    {
        var storage = new InMemoryStorage();
        var hangfire = new BackgroundJobClient(storage);
        var now = hangfire.Enqueue<LegacyMailer>(mailer => mailer.SendWelcome("ada@example.com"));
        var later = hangfire.Schedule<LegacyMailer>(mailer => mailer.SendWelcome("grace@example.com"), TimeSpan.FromHours(1));
        hangfire.Enqueue<LegacyReports>(reports => reports.Unmapped());
        var recurring = new RecurringJobManager(storage);
        recurring.AddOrUpdate<LegacyReports>("Daily Report", reports => reports.Daily(7), "0 6 * * *");
        recurring.AddOrUpdate<LegacyReports>("every-5-seconds", reports => reports.Daily(1), "*/5 * * * * *");

        var sent = new List<string>();
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .AddJob(Welcome, job =>
            {
                lock (sent)
                {
                    sent.Add(job.Input.Address);
                }

                return Task.CompletedTask;
            })
            .Stub(Report));

        var migration = Migration(storage);
        var plan = migration.Plan();
        Assert.Equal(0, plan.RunningServers);
        Assert.Equal(new[] { "Daily Report", later, now }.Order(StringComparer.Ordinal), plan.Ready.Select(item => item.HangfireId).Order(StringComparer.Ordinal));
        Assert.Equal(2, plan.Problems.Count());
        Assert.Contains(plan.Problems, item => item.Problem!.Contains("No mapping", StringComparison.Ordinal));
        Assert.Contains(plan.Problems, item => item.Problem!.Contains("minute precision", StringComparison.Ordinal));

        var result = await migration.ImportAsync(plan, host.Store, host.Store);
        Assert.Equal(3, result.Imported.Count);
        var again = await migration.ImportAsync(migration.Plan(), host.Store, host.Store);
        Assert.Equal(result.Imported.Where(i => i.JobId is not null).Select(i => i.JobId), again.Imported.Where(i => i.JobId is not null).Select(i => i.JobId));

        await host.WaitForJobAsync(result.Imported.Single(i => i.Item.HangfireId == now).JobId!.Value);
        await host.AdvanceAsync(TimeSpan.FromHours(2));
        await host.WaitForJobAsync(result.Imported.Single(i => i.Item.HangfireId == later).JobId!.Value);
        Assert.Equal(["ada@example.com", "grace@example.com"], sent.Order());
        var schedule = Assert.Single(await host.Store.SchedulesAsync());
        Assert.Equal(("hangfire.daily-report", "0 6 * * *", true), (schedule.Definition.Key, schedule.Definition.Cron, schedule.Enabled));

        migration.Retire(result);
        using var connection = storage.GetConnection();
        Assert.Equal(DeletedState.StateName, connection.GetJobData(now)!.State);
        Assert.Equal(["every-5-seconds"], connection.GetRecurringJobs().Select(job => job.Id));
    }

    [Fact]
    public async Task Import_is_refused_while_hangfire_servers_run()
    {
        var storage = new InMemoryStorage();
        new BackgroundJobClient(storage).Enqueue<LegacyMailer>(mailer => mailer.SendWelcome("ada@example.com"));
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.Stub(Welcome).Stub(Report));
        var migration = Migration(storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => migration.ImportAsync(migration.Plan() with { RunningServers = 1 }, host.Store, host.Store));
        Assert.Empty(await host.Store.ListAsync(new()));
    }
}
