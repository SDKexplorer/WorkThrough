using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ScheduleTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("test", "scheduler-owner"), "Test schedule lifecycle");
    private static ScheduleDefinition Definition() => new("reports.hourly", 1, "0 * * * *", "UTC", new("reports.generate", 1), JsonSerializer.SerializeToElement("raw input"));

    [Theory, MemberData(nameof(Providers))]
    public async Task Competing_schedulers_create_one_occurrence_and_paused_drafts_never_dispatch(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var schedule = Definition();
        await store.SaveScheduleDraftAsync(schedule, 0, Command());
        await Assert.ThrowsAsync<StateConflictException>(() => store.SetScheduleEnabledAsync(schedule.Key, 1, 1, true, Command()));
        Assert.Equal(0, await store.DispatchDueSchedulesAsync());
        await store.PublishScheduleAsync(schedule.Key, 1, 1, Command());
        await SetDueAsync(harness.ConnectionString, provider, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(0, await store.DispatchDueSchedulesAsync());
        await store.SetScheduleEnabledAsync(schedule.Key, 1, 2, true, Command());
        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => harness.NewStore().DispatchDueSchedulesAsync())));
        Assert.Equal(1, counts.Sum());
        var occurrence = Assert.Single(await store.OccurrencesAsync(schedule.Key));
        var job = (await store.GetAsync(occurrence.JobId!.Value))!;
        Assert.Equal("raw input", job.Payload.GetString());
        Assert.Equal(schedule.Processor, job.Contract);
        Assert.Equal("MisfireDispatched", occurrence.Outcome);
        await Assert.ThrowsAsync<StateConflictException>(() => store.SaveScheduleDraftAsync(schedule, 3, Command()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Coalesced_overlap_defers_one_occurrence_until_prior_work_finishes(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var definition = Definition() with { Overlap = ScheduleOverlap.Coalesce };
        await store.SaveScheduleDraftAsync(definition, 0, Command());
        await store.PublishScheduleAsync(definition.Key, 1, 1, Command());
        await store.SetScheduleEnabledAsync(definition.Key, 1, 2, true, Command());
        await SetDueAsync(harness.ConnectionString, provider, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(1, await store.DispatchDueSchedulesAsync());
        await SetDueAsync(harness.ConnectionString, provider, DateTimeOffset.UtcNow.AddMinutes(-2));
        Assert.Equal(0, await store.DispatchDueSchedulesAsync());
        Assert.Equal(0, await store.DispatchDueSchedulesAsync());
        var job = (await store.ClaimAsync(new("worker", [definition.Processor], ["default"], TimeSpan.FromSeconds(30))))!;
        await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done());
        Assert.Equal(1, await store.DispatchDueSchedulesAsync());
        Assert.Equal(2, (await store.OccurrencesAsync(definition.Key)).Count);
    }

    [Fact]
    public void Daily_schedule_has_one_occurrence_on_the_fall_back_day()
    {
        var schedule = Definition() with { Cron = "30 1 * * *", TimeZone = "America/New_York" };
        var first = ScheduleClock.Next(schedule, DateTimeOffset.Parse("2026-11-01T00:00:00Z"))!.Value;
        var second = ScheduleClock.Next(schedule, first)!.Value;
        Assert.Equal(new DateTime(2026, 11, 1), first.UtcDateTime.Date);
        Assert.Equal(new DateTime(2026, 11, 2), second.UtcDateTime.Date);
    }

    private static async Task SetDueAsync(string connectionString, string provider, DateTimeOffset due)
    {
        await using DbConnection connection = provider == "sqlite" ? new SqliteConnection(connectionString) : new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "UPDATE dj_schedules SET next_due=@due";
        var parameter = query.CreateParameter(); parameter.ParameterName = "due"; parameter.Value = due.ToUnixTimeMilliseconds(); query.Parameters.Add(parameter);
        await query.ExecuteNonQueryAsync();
    }
}
