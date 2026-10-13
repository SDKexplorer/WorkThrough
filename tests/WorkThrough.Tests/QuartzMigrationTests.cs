using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using WorkThrough.Migration.Quartz;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public sealed class SendReminderJob : Quartz.IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

[DisallowConcurrentExecution]
public sealed class NightlyCleanupJob : Quartz.IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

public sealed class UnmappedQuartzJob : Quartz.IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

public sealed record QuartzReminder(string Customer);
public sealed record Cleanup(int Days);

/// <summary>Quartz.NET jobs and triggers moving into WorkThrough.</summary>
public sealed class QuartzMigrationTests
{
    private static readonly JobDefinition<QuartzReminder> Remind = new("tests.quartz.remind");
    private static readonly JobDefinition<Cleanup> Clean = new("tests.quartz.cleanup");

    [Theory]
    [InlineData("0 0 9 ? * MON-FRI", "0 0 9 ? * MON-FRI")]
    [InlineData("0 30 6 ? * 2-6", "0 30 6 ? * 1-5")]
    [InlineData("0 0 12 ? * 1,7 *", "0 0 12 ? * 0,6")]
    [InlineData("0 0 8 ? * 6#3", "0 0 8 ? * 5#3")]
    [InlineData("0 0/15 * * * ?", "0 0/15 * * * ?")]
    public void Quartz_cron_expressions_become_six_field_schedules_with_Sunday_as_zero(string quartz, string expected) =>
        Assert.Equal(expected, QuartzMigration.CronOf(quartz).Cron);

    [Fact]
    public void Year_restricted_cron_expressions_are_reported() =>
        Assert.Contains("years", QuartzMigration.CronOf("0 0 9 * * ? 2027").Problem);

    [Fact]
    public async Task Triggers_become_schedules_and_delayed_jobs_once_and_are_retired_in_quartz()
    {
        var scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "migration-" + Guid.NewGuid().ToString("N"),
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        }).GetScheduler();
        var holidays = new HolidayCalendar();
        holidays.AddExcludedDate(new DateTime(2026, 12, 25));
        await scheduler.AddCalendar("holidays", holidays, false, false);

        var reminder = JobBuilder.Create<SendReminderJob>().WithIdentity("reminder", "crm").UsingJobData("customer", "ada").StoreDurably().Build();
        await scheduler.AddJob(reminder, false);
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("weekdays", "crm").ForJob(reminder)
            .WithCronSchedule("0 0 9 ? * 2-6", cron => cron.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest")))
            .ModifiedByCalendar("holidays").Build());
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("follow-up", "crm").ForJob(reminder).UsingJobData("customer", "grace")
            .StartAt(DateTimeOffset.UtcNow.AddHours(1)).Build());
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("paused", "crm").ForJob(reminder)
            .WithSimpleSchedule(simple => simple.WithIntervalInMinutes(30).RepeatForever()).Build());
        await scheduler.PauseTrigger(new TriggerKey("paused", "crm"));
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("only-2027", "crm").ForJob(reminder).WithCronSchedule("0 0 9 * * ? 2027").Build());

        var cleanup = JobBuilder.Create<NightlyCleanupJob>().WithIdentity("cleanup").UsingJobData("days", "30").Build();
        await scheduler.ScheduleJob(cleanup, TriggerBuilder.Create().WithIdentity("nightly")
            .WithCronSchedule("0 0 2 * * ?", cron => cron.WithMisfireHandlingInstructionDoNothing()).Build());
        await scheduler.ScheduleJob(JobBuilder.Create<UnmappedQuartzJob>().WithIdentity("unmapped").Build(),
            TriggerBuilder.Create().WithIdentity("unmapped").WithCronSchedule("0 0 3 * * ?").Build());

        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.Stub(Remind).Stub(Clean));
        var migration = new QuartzMigration(scheduler)
            .Map<SendReminderJob, QuartzReminder>(Remind, data => new QuartzReminder(data.GetString("customer")!))
            .Map<NightlyCleanupJob, Cleanup>(Clean, data => new Cleanup(data.Get<int>("days")));

        var plan = await migration.PlanAsync();
        Assert.False(plan.SchedulerRunning);
        Assert.Equal(["DEFAULT.nightly", "crm.follow-up", "crm.paused", "crm.weekdays"], plan.Ready.Select(item => item.TriggerKey).Order(StringComparer.Ordinal));
        Assert.Equal(2, plan.Problems.Count());

        var weekdays = plan.Ready.Single(item => item.TriggerName == "weekdays").Schedule!;
        Assert.Equal(("0 0 9 ? * 1-5", "Europe/Bucharest", ScheduleOverlap.Allow), (weekdays.Cron, weekdays.TimeZone, weekdays.Overlap));
        Assert.Equal([new DateOnly(2026, 12, 25)], weekdays.ExcludedDates);
        var nightly = plan.Ready.Single(item => item.TriggerName == "nightly").Schedule!;
        Assert.Equal((ScheduleOverlap.Coalesce, ScheduleMisfire.Skip), (nightly.Overlap, nightly.Misfire));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.Ready.Single(item => item.TriggerName == "paused").Schedule!.Interval);

        var result = await migration.ImportAsync(plan, host.Store, host.Store);
        var again = await migration.ImportAsync(await migration.PlanAsync(), host.Store, host.Store);
        Assert.Equal(4, result.Imported.Count);
        Assert.Equal(result.Imported.Single(i => i.JobId is not null).JobId, again.Imported.Single(i => i.JobId is not null).JobId);

        var followUp = (await host.Queries.GetAsync(result.Imported.Single(i => i.JobId is not null).JobId!.Value))!;
        Assert.Equal(("grace", JobStatus.Queued), (followUp.Payload.GetProperty("customer").GetString(), followUp.Status));
        var schedules = (await host.Store.SchedulesAsync()).ToDictionary(schedule => schedule.Definition.Key);
        Assert.Equal(["quartz.crm.paused", "quartz.crm.weekdays", "quartz.default.nightly"], schedules.Keys.Order(StringComparer.Ordinal));
        Assert.False(schedules["quartz.crm.paused"].Enabled);
        Assert.True(schedules["quartz.crm.weekdays"].Enabled);

        await migration.RetireAsync(result);
        Assert.Equal(["DEFAULT.unmapped", "crm.only-2027"], (await scheduler.GetTriggerKeys(Quartz.Impl.Matchers.GroupMatcher<TriggerKey>.AnyGroup()))
            .Select(key => key.ToString()).Order(StringComparer.Ordinal));
        await scheduler.Shutdown();
    }
}
