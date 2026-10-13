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

    [Fact]
    public void Calendars_skip_weekdays_yearly_dates_days_of_the_month_daily_ranges_and_cron_matches()
    {
        var hourly = Cron("0 * * * *");
        var start = new DateTimeOffset(2026, 12, 24, 22, 30, 0, TimeSpan.Zero);

        // Thursday 24 December, 22:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 12, 26, 0, 0, 0, TimeSpan.Zero),
            ScheduleClock.Next(hourly.Excluding(new ScheduleCalendar().ExcludingTimes(new(23, 0), new(0, 0)).ExcludingEveryYear(12, 25)), start));
        Assert.Equal(new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero),
            ScheduleClock.Next(hourly.Excluding(new ScheduleCalendar().ExcludingWeekdays(DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday)
                .ExcludingDaysOfMonth(24)), start));
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 8, 0, 0, TimeSpan.Zero),
            ScheduleClock.Next(hourly.Excluding(new ScheduleCalendar().ExcludingCron("* * 0-7 * * *")), start.AddHours(1)));
    }

    [Fact]
    public void Daily_windows_repeat_inside_their_hours_on_their_days()
    {
        var window = ScheduleDefinition.Every("tests.rich", TimeSpan.FromMinutes(30), Ticks, new Tick(1))
            .Within(new(9, 0), new(17, 0), DayOfWeek.Monday, DayOfWeek.Wednesday);

        Assert.Equal(Monday.AddHours(9), ScheduleClock.Next(window, Monday));
        Assert.Equal(Monday.AddHours(9.5), ScheduleClock.Next(window, Monday.AddHours(9)));
        Assert.Equal(Monday.AddHours(17), ScheduleClock.Next(window, Monday.AddHours(16.75)));
        Assert.Equal(Monday.AddDays(2).AddHours(9), ScheduleClock.Next(window, Monday.AddHours(17)));
    }

    [Fact]
    public void Calendar_intervals_keep_local_time_across_daylight_saving_and_month_ends()
    {
        // 09:00 in Bucharest is 06:00 UTC until 25 October 2026, then 07:00 UTC.
        var daily = ScheduleDefinition.EveryCalendar("tests.rich", 1, CalendarUnit.Day, new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), Ticks,
            new Tick(1), "Europe/Bucharest");
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.Zero), ScheduleClock.Next(daily, new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero), ScheduleClock.Next(daily, new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));

        var monthly = ScheduleDefinition.EveryCalendar("tests.rich", 1, CalendarUnit.Month, new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero), Ticks, new Tick(1));
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero), ScheduleClock.Next(monthly, new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
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
