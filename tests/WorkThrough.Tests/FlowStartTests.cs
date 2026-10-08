using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace WorkThrough.Tests;

public sealed class FlowStartTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static FlowDefinition Definition() => new FlowBuilder("tests.start", 1)
        .Step<Alpha>("first", new Alpha("a"))
        .Step<Beta>("next", new Beta("b")).After("first").Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task Concurrent_retries_create_one_run_and_one_set_of_steps(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var definition = Definition();
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new WorkflowClient(harness.NewStore()).StartAsync(definition, new
            {
                order = 123
            }, "order:123", "start:123"))));
        var id = Assert.Single(ids.Distinct());
        var jobs = await harness.Store.ListFlowJobsAsync(id);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, (await harness.Store.ListAsync(new())).Count);
        Assert.Single(jobs, j => j.Status == JobStatus.Queued);
        Assert.Single(jobs, j => j.Status == JobStatus.Waiting);

        var restarted = harness.NewStore();
        await restarted.InitializeAsync();
        await restarted.RetireAsync(definition.Key, 1, 1,
            new(Guid.NewGuid(), new("test", "maintainer"), "Prevent new starts"));
        Assert.Equal(id, await new WorkflowClient(restarted).StartAsync(definition, new
        {
            order = 123
        }, "order:123", "start:123"));
        await Assert.ThrowsAsync<StateConflictException>(() =>
            new WorkflowClient(restarted).StartAsync(definition, new
            {
                order = 123
            }, "order:123", "another-request"));
        Assert.Equal(jobs.Select(j => j.Id).Order(), (await restarted.ListFlowJobsAsync(id)).Select(j => j.Id).Order());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Conflicting_key_reuse_rejects_input_correlation_and_definition_changes(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new WorkflowClient(harness.Store);
        var definition = Definition();
        await client.StartAsync(definition, new
        {
            order = 123
        }, "order:123", "start:123");
        await Assert.ThrowsAsync<StateConflictException>(() => client.StartAsync(definition, new { order = 456 }, "order:123", "start:123"));
        await Assert.ThrowsAsync<StateConflictException>(() => client.StartAsync(definition, new { order = 123 }, "other", "start:123"));
        await Assert.ThrowsAsync<StateConflictException>(() => client.StartAsync(definition with { Version = 2 }, new { order = 123 }, "order:123", "start:123"));
        await Assert.ThrowsAsync<StateConflictException>(() => client.StartAsync(definition with { FailurePolicy = FlowFailurePolicy.Stop }, new { order = 123 }, "order:123", "start:123"));
        Assert.Null(await harness.Store.GetDefinitionAsync(definition.Key, 2));
        Assert.Equal(2, (await harness.Store.ListAsync(new())).Count);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Correlation_alone_does_not_deduplicate_and_invalid_keys_create_no_work(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new WorkflowClient(harness.Store);
        foreach (var invalid in new[] { "", " ", new string('x', 257) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(Definition(), new { }, "same", invalid));
        }

        Assert.Empty(await harness.Store.ListAsync(new()));
        var first = await client.StartAsync(Definition(), new
        {
        }, "same");
        var second = await client.StartAsync(Definition(), new
        {
        }, "same");
        Assert.NotEqual(first, second);
        Assert.Equal(4, (await harness.Store.ListAsync(new("same"))).Count);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Receipt_write_failure_rolls_back_the_run_steps_and_definition(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using DbConnection connection = provider == "postgres"
            ? new NpgsqlConnection(harness.ConnectionString) : new SqliteConnection(harness.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = provider == "postgres" ? """
            CREATE FUNCTION reject_start() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected start receipt failure'; END; $$;
            CREATE TRIGGER reject_start BEFORE INSERT ON dj_flow_starts FOR EACH ROW EXECUTE FUNCTION reject_start();
            """ : """
            CREATE TRIGGER reject_start BEFORE INSERT ON dj_flow_starts
            BEGIN SELECT RAISE(ABORT, 'injected start receipt failure'); END;
            """;
        await command.ExecuteNonQueryAsync();
        var client = new WorkflowClient(harness.Store);
        await Assert.ThrowsAnyAsync<DbException>(() => client.StartAsync(Definition(), new { }, null, "retry-after-failure"));
        Assert.Empty(await harness.Store.ListAsync(new()));
        Assert.Empty(await harness.Store.DefinitionsAsync());
        command.CommandText = "SELECT COUNT(*) FROM dj_flows";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = provider == "postgres" ? "DROP TRIGGER reject_start ON dj_flow_starts" : "DROP TRIGGER reject_start";
        await command.ExecuteNonQueryAsync();
        var run = await client.StartAsync(Definition(), new
        {
        }, null, "retry-after-failure");
        Assert.Equal(run, await client.StartAsync(Definition(), new
        {
        }, null, "retry-after-failure"));
        Assert.Equal(2, (await harness.Store.ListFlowJobsAsync(run)).Count);
    }
}
