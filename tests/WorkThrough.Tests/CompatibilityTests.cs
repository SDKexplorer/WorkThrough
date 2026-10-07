using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace WorkThrough.Tests;

public sealed class CompatibilityTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task Schema_one_fixtures_upgrade_without_rewriting_invocations_or_flow_semantics(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider, initialize: false);
        await using DbConnection connection = provider == "sqlite" ? new SqliteConnection(harness.ConnectionString) : new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema1.sql"));
        await query.ExecuteNonQueryAsync();
        await harness.Store.InitializeAsync();
        await harness.NewStore().InitializeAsync();
        var job = (await harness.Store.GetAsync(Guid.Parse("11111111-1111-1111-1111-111111111111")))!;
        Assert.Equal(new JobContractId("tests.alpha", 1), job.Contract);
        Assert.Equal("{\"value\":\"schema-one\"}", job.Payload.GetRawText());
        var definition = (await harness.Store.GetDefinitionAsync("fixtures.flow", 1))!;
        Assert.Equal(DefinitionStatus.Published, definition.Status);
        Assert.Equal(FlowFailurePolicy.Stop, definition.Definition.FailurePolicy);
        var claim = (await harness.Store.ClaimAsync(new("new-worker", [job.Contract], ["default"], TimeSpan.FromSeconds(30))))!;
        Assert.Equal(job.Id, claim.Id);
        Assert.True(await harness.Store.CompleteAsync(job.Id, claim.LeaseToken!.Value, JobResult.Done()));
        query.CommandText = "INSERT INTO dj_schema(version) VALUES (999)";
        await query.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Store.InitializeAsync());
    }
}
