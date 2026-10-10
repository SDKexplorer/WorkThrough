using Xunit;

namespace WorkThrough.Tests;

public sealed class EventTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task Event_cursors_survive_restart_and_trimming_preserves_monotonic_sequences(string provider)
    {
        await using var database = await StoreHarness.CreateAsync(provider);
        var store = database.Store;
        var id = await new JobClient(store).EnqueueAsync(new Alpha("events"));
        var job = (await store.ClaimAsync(new("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromSeconds(30))))!;
        await store.CompleteAsync(id, job.LeaseToken!.Value, JobResult.Done());
        Assert.Equal(0, await store.SubscribeAsync("billing"));
        var events = await store.ReadEventsAsync(0);
        var last = events[^1].Sequence;
        Assert.True(await store.AcknowledgeAsync("billing", 0, last));
        Assert.False(await store.AcknowledgeAsync("billing", 0, last));
        Assert.Equal(last, await database.NewStore().SubscribeAsync("billing"));
        await store.TrimEventsAsync(last, Command()).FailsAsync(ErrorType.Conflict);
        await store.SubscribeAsync("slow-exporter");
        await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddMinutes(1)), Command()).SucceedsAsync();
        await store.TrimEventsAsync(last, Command()).FailsAsync(ErrorType.Conflict);
        Assert.True(await store.AcknowledgeAsync("slow-exporter", 0, last));
        Assert.Equal(events.Count, await store.TrimEventsAsync(last, Command()).SucceedsAsync());
        await Assert.ThrowsAsync<StateConflictException>(() => store.ReadEventsAsync(0));
        await Assert.ThrowsAsync<StateConflictException>(() => store.SubscribeAsync("new-reader"));
        Assert.Equal(last, await store.SubscribeAsync("new-reader", last));
        var next = await new JobClient(store).EnqueueAsync(new Alpha("after trim"));
        await store.ClaimAsync(new("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromSeconds(30)));
        var newer = await store.ReadEventsAsync(last);
        Assert.All(newer, item => Assert.True(item.Sequence > last));
        Assert.Contains(newer, item => item.JobId == next);
    }

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "events"), "Verify retained event history");
}
