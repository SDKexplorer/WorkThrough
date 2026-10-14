using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using WorkThrough;
using WorkThrough.Migration.Quartz;
using WorkThrough.Storage;

// An existing Quartz application: weekday reminders except on holidays, and a cleanup every six hours. Its in-memory store stands in
// for your ADO.NET job store; the scheduler is not started, as during a cutover.
var quartz = await new StdSchedulerFactory(new NameValueCollection { ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz" }).GetScheduler();
var holidays = new HolidayCalendar();
holidays.AddExcludedDate(new DateTime(2026, 12, 25));
await quartz.AddCalendar("holidays", holidays, false, false);
await quartz.ScheduleJob(JobBuilder.Create<ReminderJob>().WithIdentity("reminders", "crm").UsingJobData("audience", "customers").Build(),
    TriggerBuilder.Create().WithIdentity("weekdays", "crm").WithCronSchedule("0 0 9 ? * MON-FRI").ModifiedByCalendar("holidays").Build());
await quartz.ScheduleJob(JobBuilder.Create<CleanupJob>().WithIdentity("cleanup").UsingJobData("days", "30").Build(),
    TriggerBuilder.Create().WithIdentity("every-six-hours").WithSimpleSchedule(simple => simple.WithIntervalInHours(6).RepeatForever()).Build());

// The WorkThrough side: an address and an input per job.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkThrough()
    .UsePostgreSql(Environment.GetEnvironmentVariable("WORKTHROUGH_POSTGRES")
        ?? "Host=127.0.0.1;Port=5432;Database=workthrough;Username=postgres;Password=workthrough;Search Path=quartz_migration")
    .Job(Reminders.Send, job => job.Run(call =>
    {
        Console.WriteLine($"Reminding {call.Input.Audience}");

        return Task.CompletedTask;
    }))
    .Job(Maintenance.Cleanup, job => job.Run(call =>
    {
        Console.WriteLine($"Removing data older than {call.Input.Days} days");

        return Task.CompletedTask;
    }))
    .AddScheduler()
    .AddWorker();
using var host = builder.Build();
await host.StartAsync();

// Map each Quartz job class, review the plan, import, then unschedule the imported triggers in Quartz.
var migration = new QuartzMigration(quartz)
    .Map<ReminderJob, Reminder>(Reminders.Send, data => new Reminder(data.GetString("audience")!))
    .Map<CleanupJob, Cleanup>(Maintenance.Cleanup, data => new Cleanup(data.Get<int>("days")));
var plan = await migration.PlanAsync();
foreach (var item in plan.Items)
{
    var timing = item.Schedule is { } schedule ? schedule.Interval?.ToString() ?? $"cron {schedule.Cron}" : item.Problem;
    Console.WriteLine($"{item.TriggerKey,-22} {item.JobType} -> {item.Target?.ToString() ?? item.Problem} ({timing})");
}

var store = (RelationalJobStore)host.Services.GetRequiredService<IJobStore>();
var result = await migration.ImportAsync(plan, store, store);
await migration.RetireAsync(result);
foreach (var schedule in await store.SchedulesAsync())
{
    Console.WriteLine($"Schedule {schedule.Definition.Key}: next at {schedule.NextOccurrence:u}, enabled {schedule.Enabled}");
}

Console.WriteLine($"Imported {result.Imported.Count}, skipped {result.Skipped.Count}.");
await host.StopAsync();
await quartz.Shutdown();

public sealed record Reminder(string Audience);
public sealed record Cleanup(int Days);

public static class Reminders
{
    public static JobDefinition<Reminder> Send { get; } = new("crm.reminders.send");
}

public static class Maintenance
{
    public static JobDefinition<Cleanup> Cleanup { get; } = new("maintenance.cleanup");
}

public sealed class ReminderJob : Quartz.IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

public sealed class CleanupJob : Quartz.IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
