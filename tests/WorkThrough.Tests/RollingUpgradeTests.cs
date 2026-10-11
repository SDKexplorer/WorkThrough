using System.Data.Common;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>Hosts of different releases sharing one store (ADR-021).</summary>
public sealed class RollingUpgradeTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    // A schema version no release has used.
    private const long FutureSchema = 1000;

    private static ClaimRequest Claim(string worker = "worker") => new(worker, [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1));

    [Theory, MemberData(nameof(Providers))]
    public async Task A_newer_schema_that_still_accepts_this_build_is_used_without_migrating(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await ExecuteAsync(harness, provider == "oracle" ? $"UPDATE dj_schema SET version={FutureSchema}" : $"INSERT INTO dj_schema(version) VALUES ({FutureSchema})");

        await harness.NewStore().InitializeAsync();
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("from an older host"));
        Assert.Equal(id, (await harness.Store.ClaimAsync(Claim()))!.Id);
        Assert.Equal(FutureSchema, Convert.ToInt64(await ScalarAsync(harness, "SELECT MAX(version) FROM dj_schema")));

        await ExecuteAsync(harness, $"UPDATE dj_compatibility SET reader_minimum={FutureSchema}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.NewStore().InitializeAsync());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_new_store_starts_at_this_builds_protocol(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);

        var state = await harness.Store.EngineProtocolAsync();
        Assert.Equal((EngineProtocol.Supported, EngineProtocol.Supported), (state.Active, state.Supported));
        Assert.Empty(state.Blockers);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_store_above_this_builds_protocol_refuses_claims_and_administration(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("queued"));
        await ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.Supported + 1}");

        var refused = await Assert.ThrowsAsync<EngineProtocolException>(() => harness.Store.ClaimAsync(Claim()));
        Assert.Equal(EngineProtocol.Supported + 1, refused.Active);
        await Assert.ThrowsAsync<EngineProtocolException>(() => harness.Store.CancelAsync(id));
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(id))!.Status);
    }

    internal static async Task ExecuteAsync(StoreHarness harness, string sql)
    {
        await using DbConnection connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(StoreHarness harness, string sql)
    {
        await using DbConnection connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
