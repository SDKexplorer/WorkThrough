using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class NamedCalendarTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private const string Holidays = "holidays";
    private static readonly JobDefinition<Tick> Ticks = new("tests.calendar.tick");

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Calendar change");

    private static ScheduleDefinition Daily() => ScheduleDefinition.Create("tests.calendar", "0 9 * * *", Ticks, new Tick(1)).ExcludingCalendar(Holidays);

    [Theory, MemberData(nameof(Providers))]
    public async Task Editing_a_named_calendar_moves_the_schedules_that_name_it(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        await store.SaveCalendarAsync(Holidays, new ScheduleCalendar(), 0, Command()).SucceedsAsync();
        await store.SaveScheduleDraftAsync(Daily(), 0, Command()).SucceedsAsync();
        await store.PublishScheduleAsync("tests.calendar", 1, 1, Command()).SucceedsAsync();
        var first = (await NextAsync(store))!.Value;

        var skipFirst = new ScheduleCalendar().ExcludingDates(DateOnly.FromDateTime(first.UtcDateTime));
        Assert.Equal(2, await Revision(store.SaveCalendarAsync(Holidays, skipFirst, 1, Command())));
        Assert.Equal(first.AddDays(1), await NextAsync(store));

        Assert.Equal(3, await Revision(store.SaveCalendarAsync(Holidays, new ScheduleCalendar(), 2, Command())));
        Assert.Equal(first, await NextAsync(store));

        await store.SaveCalendarAsync(Holidays, skipFirst, 1, Command()).FailsAsync(ErrorType.Conflict);
        var saved = Assert.Single(await store.CalendarsAsync());
        Assert.Equal((Holidays, 3), (saved.Name, saved.Revision));
        Assert.Equal(3, (await store.AuditAsync()).Count(entry => entry.Action == AuditActions.SaveCalendar && entry.Outcome == AuditOutcomes.Applied));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_schedule_cannot_run_without_its_calendars(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        await store.SaveScheduleDraftAsync(Daily(), 0, Command()).SucceedsAsync();
        await store.PublishScheduleAsync("tests.calendar", 1, 1, Command()).FailsAsync(ErrorType.Conflict);

        await store.SaveCalendarAsync(Holidays, new ScheduleCalendar(), 0, Command()).SucceedsAsync();
        await store.DeleteCalendarAsync(Holidays, 1, Command()).FailsAsync(ErrorType.Conflict);
        await store.PublishScheduleAsync("tests.calendar", 1, 1, Command()).SucceedsAsync();
        await store.SetScheduleEnabledAsync("tests.calendar", 1, 2, true, Command()).SucceedsAsync();
        await store.DeleteCalendarAsync(Holidays, 1, Command()).FailsAsync(ErrorType.Conflict);

        await store.SetScheduleEnabledAsync("tests.calendar", 1, 3, false, Command()).SucceedsAsync();
        await store.DeleteCalendarAsync(Holidays, 1, Command()).SucceedsAsync();
        Assert.Empty(await store.CalendarsAsync());
        await store.SetScheduleEnabledAsync("tests.calendar", 1, 4, true, Command()).FailsAsync(ErrorType.Conflict);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Schedules_naming_calendars_wait_for_their_protocol(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.RichSchedules}");

        await harness.Store.SaveScheduleDraftAsync(Daily(), 0, Command()).FailsAsync(ErrorType.Conflict);
        await harness.Store.SaveScheduleDraftAsync(Daily() with { Calendars = null }, 0, Command()).SucceedsAsync();
    }

    [Fact]
    public void Named_calendars_fold_into_the_schedule_calendar()
    {
        var weekend = new ScheduleCalendar().ExcludingWeekdays(DayOfWeek.Saturday, DayOfWeek.Sunday);
        var christmas = new ScheduleCalendar().ExcludingEveryYear(12, 25);
        var definition = Daily().Excluding(christmas).ExcludingCalendar("weekend");
        var thursday = new DateTimeOffset(2026, 12, 24, 10, 0, 0, TimeSpan.Zero);

        var resolved = ScheduleClock.WithCalendars(definition, new Dictionary<string, ScheduleCalendar> { [Holidays] = new(), ["weekend"] = weekend });

        Assert.Equal(new DateTimeOffset(2026, 12, 28, 9, 0, 0, TimeSpan.Zero), ScheduleClock.Next(resolved, thursday));
        Assert.Throws<InvalidOperationException>(() => ScheduleClock.WithCalendars(definition, new Dictionary<string, ScheduleCalendar>()));
    }

    private static async Task<DateTimeOffset?> NextAsync(RelationalJobStore store) => Assert.Single(await store.SchedulesAsync()).NextOccurrence;

    private static async Task<int> Revision(Task<Result<int>> command)
    {
        var result = await command;
        Assert.True(result.IsSuccess, result.Error?.Message);

        return result.Value;
    }
}
