using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using Quartz.Listener;
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
        var holidays = new HolidayCalendar { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest") };
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
        Assert.Equal([new DateOnly(2026, 12, 25)], weekdays.Calendar!.Dates);
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

    [Fact]
    public async Task Chained_jobs_become_a_schedule_that_starts_a_flow()
    {
        var scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "chain-" + Guid.NewGuid().ToString("N"),
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        }).GetScheduler();
        var reminder = JobBuilder.Create<SendReminderJob>().WithIdentity("reminder", "crm").UsingJobData("customer", "ada").Build();
        var cleanup = JobBuilder.Create<NightlyCleanupJob>().WithIdentity("cleanup", "crm").UsingJobData("days", "30").StoreDurably().Build();
        await scheduler.AddJob(cleanup, false);
        await scheduler.ScheduleJob(reminder, TriggerBuilder.Create().WithIdentity("daily", "crm").WithCronSchedule("0 0 9 * * ?").Build());
        var chaining = new JobChainingJobListener("chain");
        chaining.AddJobChainLink(reminder.Key, cleanup.Key);
        scheduler.ListenerManager.AddJobListener(chaining);
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.Stub(Remind).Stub(Clean));
        var migration = new QuartzMigration(scheduler)
            .Map<SendReminderJob, QuartzReminder>(Remind, data => new QuartzReminder(data.GetString("customer")!))
            .Map<NightlyCleanupJob, Cleanup>(Clean, data => new Cleanup(data.Get<int>("days")));

        var plan = await migration.PlanAsync();
        var item = Assert.Single(plan.Ready);
        Assert.Empty(plan.Problems);
        Assert.Equal(ScheduledFlows.Start.Identity, item.Schedule!.Processor);
        Assert.Equal(["job-crm.reminder", "job-crm.cleanup"], item.Flow!.Steps.Select(step => step.Id));

        await migration.ImportAsync(plan, host.Store, host.Store);
        var schedule = Assert.Single(await host.Store.SchedulesAsync());
        await host.AdvanceAsync(schedule.NextOccurrence!.Value - host.UtcNow + TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(WorkThroughTestHost.DefaultTimeout);
        IReadOnlyList<JobRecord> steps = [];
        while (steps.Count(job => job.Status == JobStatus.Succeeded) < 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            steps = (await host.Queries.ListAsync(new(Limit: 50), timeout.Token)).Where(job => job.WorkflowRunId is not null).ToList();
        }

        Assert.Equal(["job-crm.cleanup", "job-crm.reminder"], steps.Select(job => job.StepId).Order());
        await scheduler.Shutdown();
    }

    [Fact]
    public async Task Daily_windows_calendar_intervals_and_chained_calendars_import()
    {
        var scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "migration-" + Guid.NewGuid().ToString("N"),
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        }).GetScheduler();
        var utc = TimeZoneInfo.Utc;
        var holidays = new HolidayCalendar { TimeZone = utc };
        holidays.AddExcludedDate(new DateTime(2026, 12, 25));
        var weekends = new WeeklyCalendar(holidays) { TimeZone = utc };
        var nights = new DailyCalendar(weekends, "22:00", "23:59:59:999") { TimeZone = utc };
        await scheduler.AddCalendar("office", nights, false, false);

        var job = JobBuilder.Create<SendReminderJob>().WithIdentity("office").UsingJobData("customer", "ops").StoreDurably().Build();
        await scheduler.AddJob(job, false);
        await scheduler.AddJob(JobBuilder.Create<SendReminderJob>().WithIdentity("idle").StoreDurably().Build(), false);
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("office-hours").ForJob(job)
            .WithDailyTimeIntervalSchedule(daily => daily.WithIntervalInMinutes(30).OnMondayThroughFriday()
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 0)).EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(17, 0)).InTimeZone(utc))
            .Build());
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("monthly").ForJob(job).StartAt(new DateTimeOffset(2026, 1, 31, 8, 0, 0, TimeSpan.Zero))
            .WithCalendarIntervalSchedule(calendar => calendar.WithIntervalInMonths(1).InTimeZone(utc)).ModifiedByCalendar("office").Build());

        var plan = await new QuartzMigration(scheduler).Map<SendReminderJob, QuartzReminder>(Remind, data => new QuartzReminder(data.GetString("customer")!))
            .PlanAsync();

        Assert.Equal("The job has no triggers; nothing fires it.", Assert.Single(plan.Problems).Problem);
        var office = plan.Ready.Single(item => item.TriggerName == "office-hours").Schedule!;
        Assert.Equal((TimeSpan.FromMinutes(30), new TimeOnly(9, 0), new TimeOnly(17, 0)), (office.Interval, office.Window!.From, office.Window.Until));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], office.Window.Days!.Order());
        var monthly = plan.Ready.Single(item => item.TriggerName == "monthly").Schedule!;
        Assert.Equal(new CalendarInterval(1, CalendarUnit.Month), monthly.CalendarInterval);
        Assert.Equal([new DateOnly(2026, 12, 25)], monthly.Calendar!.Dates);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], monthly.Calendar.Weekdays!.Order());
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(0, 0)), (monthly.Calendar.DailyFrom, monthly.Calendar.DailyUntil));

        // 28 February 2026 is a Saturday, so the month-end run moves past the weekend.
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 8, 0, 0, TimeSpan.Zero), ScheduleClock.Next(monthly, new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        await scheduler.Shutdown();
    }
}
