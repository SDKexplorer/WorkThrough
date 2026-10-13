using System.Text.Json;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public sealed record Tick(int Number);

/// <summary>Schedules with seconds, intervals, bounds and excluded dates (ADR-030).</summary>
public sealed class RichScheduleTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static readonly JobDefinition<Tick> Ticks = new("tests.rich.tick");
    private static readonly DateTimeOffset Monday = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static ScheduleDefinition Cron(string cron, string zone = "UTC") => ScheduleDefinition.Create("tests.rich", cron, Ticks, new Tick(1), zone);

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Schedule ticks");

    [Fact]
    public void Six_field_cron_fires_on_seconds_and_five_fields_still_mean_minutes()
    {
        Assert.Equal(Monday.AddSeconds(30), ScheduleClock.Next(Cron("*/30 * * * * *"), Monday.AddSeconds(5)));
        Assert.Equal(Monday.AddMinutes(1), ScheduleClock.Next(Cron("* * * * *"), Monday.AddSeconds(5)));
        Assert.Equal(EngineProtocol.RichSchedules, Cron("0 * * * * *").RequiredProtocol);
        Assert.Equal(EngineProtocol.Baseline, Cron("* * * * *").RequiredProtocol);
    }

    [Fact]
    public void Intervals_count_from_their_start_and_respect_their_end()
    {
        var every = ScheduleDefinition.Every("tests.rich", TimeSpan.FromMinutes(15), Ticks, new Tick(1)).Starting(Monday.AddMinutes(7)).Ending(Monday.AddHours(1));

        Assert.Equal(Monday.AddMinutes(7), ScheduleClock.Next(every, Monday));
        Assert.Equal(Monday.AddMinutes(22), ScheduleClock.Next(every, Monday.AddMinutes(7)));
        Assert.Equal(Monday.AddMinutes(52), ScheduleClock.Next(every, Monday.AddMinutes(40)));
        Assert.Null(ScheduleClock.Next(every, Monday.AddMinutes(52)));
        Assert.Throws<ArgumentException>(() => ScheduleClock.Validate(
            ScheduleDefinition.Every("tests.rich", TimeSpan.FromMilliseconds(500), Ticks, new Tick(1))));
    }

    [Fact]
    public void Excluded_dates_skip_whole_days_in_the_schedule_time_zone()
    {
        // 09:00 every weekday in Bucharest (UTC+3 in October); Tuesday is a holiday.
        var weekdays = Cron("0 9 * * MON-FRI", "Europe/Bucharest").Excluding(new DateOnly(2026, 10, 13));

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero), ScheduleClock.Next(weekdays, Monday.AddHours(7)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Rich_schedules_wait_for_their_protocol(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.FailureBranches}");

        await harness.Store.SaveScheduleDraftAsync(Cron("0 * * * * *"), 0, Command()).FailsAsync(ErrorType.Conflict);
        await harness.Store.SaveScheduleDraftAsync(Cron("0 * * * *"), 0, Command()).SucceedsAsync();
    }

    [Fact]
    public async Task An_interval_schedule_runs_its_job_each_period()
    {
        var ticks = 0;
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Job(Ticks, job => job
                .Run(_ =>
                {
                    Interlocked.Increment(ref ticks);

                    return Task.CompletedTask;
                })
                .Every(TimeSpan.FromMinutes(10), new Tick(1))));

        var schedule = Assert.Single(await host.Store.SchedulesAsync());
        Assert.Equal((TimeSpan.FromMinutes(10), ""), (schedule.Definition.Interval, schedule.Definition.Cron));
        await host.AdvanceAsync(TimeSpan.FromMinutes(10));
        using var timeout = new CancellationTokenSource(WorkThroughTestHost.DefaultTimeout);
        while (Volatile.Read(ref ticks) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }

        Assert.Equal(JsonSerializer.SerializeToElement(new Tick(1), JobJson.Options).GetRawText(), schedule.Definition.Input.GetRawText());
    }
}
