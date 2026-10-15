using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using System.Text.Json;
using WorkThrough.Migration.Hangfire;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public class LegacyMailer
{
    public void SendWelcome(string address) => throw new NotSupportedException(address);
}

public sealed record LegacyInvoice(string Number, decimal Total);

public class LegacyBilling
{
    public void Send(LegacyInvoice invoice, int copies) => throw new NotSupportedException(invoice.Number + copies);
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
        Assert.Equal(new[] { "Daily Report", "every-5-seconds", later, now }.Order(StringComparer.Ordinal),
            plan.Ready.Select(item => item.HangfireId).Order(StringComparer.Ordinal));
        Assert.Contains("No mapping", Assert.Single(plan.Problems).Problem);
        Assert.Equal("*/5 * * * * *", plan.Ready.Single(item => item.HangfireId == "every-5-seconds").Cron);

        var result = await migration.ImportAsync(plan, host.Store, host.Store);
        Assert.Equal(4, result.Imported.Count);
        var again = await migration.ImportAsync(migration.Plan(), host.Store, host.Store);
        Assert.Equal(result.Imported.Where(i => i.JobId is not null).Select(i => i.JobId), again.Imported.Where(i => i.JobId is not null).Select(i => i.JobId));

        await host.WaitForJobAsync(result.Imported.Single(i => i.Item.HangfireId == now).JobId!.Value);
        await host.AdvanceAsync(TimeSpan.FromHours(2));
        await host.WaitForJobAsync(result.Imported.Single(i => i.Item.HangfireId == later).JobId!.Value);
        Assert.Equal(["ada@example.com", "grace@example.com"], sent.Order());
        var schedules = (await host.Store.SchedulesAsync()).ToDictionary(schedule => schedule.Definition.Key);
        Assert.Equal(("0 6 * * *", true), (schedules["hangfire.daily-report"].Definition.Cron, schedules["hangfire.daily-report"].Enabled));
        Assert.Equal("*/5 * * * * *", schedules["hangfire.every-5-seconds"].Definition.Cron);

        migration.Retire(result);
        using var connection = storage.GetConnection();
        Assert.Equal(DeletedState.StateName, connection.GetJobData(now)!.State);
        Assert.Empty(connection.GetRecurringJobs());
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

    [Fact]
    public async Task Waiting_continuations_are_reported_and_stop_the_import_until_left_explicitly()
    {
        var storage = new InMemoryStorage();
        var hangfire = new BackgroundJobClient(storage);
        var parent = hangfire.Schedule<LegacyMailer>(mailer => mailer.SendWelcome("ada@example.com"), TimeSpan.FromHours(1));
        hangfire.ContinueJobWith<LegacyMailer>(parent, mailer => mailer.SendWelcome("grace@example.com"));
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.Stub(Welcome).Stub(Report));

        var plan = Migration(storage).Plan();
        Assert.Equal(1, plan.Awaiting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Migration(storage).ImportAsync(plan, host.Store, host.Store));
        Assert.Empty(await host.Store.ListAsync(new()));

        var result = await Migration(storage).LeaveAwaitingContinuations().ImportAsync(plan, host.Store, host.Store);
        Assert.Single(result.Imported);
    }

    private static readonly JobDefinition<InvoiceCopy> Bill = new("tests.hangfire.bill");

    public sealed record InvoiceCopy(string Number, decimal Total, int Copies);

    [Fact]
    public async Task Jobs_map_by_stored_names_without_their_types_and_failed_jobs_run_again()
    {
        var storage = new InMemoryStorage();
        var hangfire = new BackgroundJobClient(storage);
        var queued = hangfire.Enqueue<LegacyBilling>(billing => billing.Send(new LegacyInvoice("INV-7", 120.5m), 2));
        // Without filters, so Hangfire's automatic retry does not turn the failure into a scheduled retry.
        var failed = new BackgroundJobClient(storage, new Hangfire.Common.JobFilterCollection())
            .Create<LegacyBilling>(billing => billing.Send(new LegacyInvoice("INV-8", 9m), 1), new FailedState(new InvalidOperationException("smtp down")));
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.Stub(Bill));

        var migration = new HangfireMigration(storage)
            .MapByName(typeof(LegacyBilling).FullName!, nameof(LegacyBilling.Send), Bill,
                call => new InvoiceCopy(call.Argument<LegacyInvoice>(0).Number, call.Argument<LegacyInvoice>(0).Total, call.Argument<int>(1)));
        Assert.Equal([queued], migration.Plan().Ready.Select(item => item.HangfireId));

        var plan = migration.IncludeFailed().Plan();
        Assert.Equal([HangfireJobKind.Enqueued, HangfireJobKind.Failed], plan.Ready.OrderBy(item => item.Kind).Select(item => item.Kind));
        var result = await migration.ImportAsync(plan, host.Store, host.Store);

        var inputs = new List<InvoiceCopy>();
        foreach (var imported in result.Imported)
        {
            inputs.Add((await host.WaitForJobAsync(imported.JobId!.Value)).Payload.Deserialize<InvoiceCopy>(JobJson.Options)!);
        }

        Assert.Equal([new("INV-7", 120.5m, 2), new("INV-8", 9m, 1)], inputs.OrderBy(input => input.Number));
        migration.Retire(result);
        using var connection = storage.GetConnection();
        Assert.Equal(DeletedState.StateName, connection.GetJobData(failed)!.State);
    }
}
