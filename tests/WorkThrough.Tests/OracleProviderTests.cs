using Oracle.ManagedDataAccess.Client;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class OracleProviderTests
{
    public static IEnumerable<object[]> Databases() => [["oracle"]];

    [Theory]
    [InlineData("SYS")]
    [InlineData("SYSTEM")]
    [InlineData("quoted-schema")]
    [InlineData("A;DROP USER B")]
    public void Dedicated_unquoted_schema_is_required(string schema) =>
        Assert.Throws<ArgumentException>(() => new OracleJobStore("User Id=owner;Data Source=localhost/FREEPDB1", new(Schema: schema)));

    [OracleTheory, MemberData(nameof(Databases))]
    public async Task Concurrent_initializers_and_resuming_a_ddl_checkpoint_preserve_identity(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider, initialize: false);
        await Task.WhenAll(harness.NewStore().InitializeAsync(), harness.NewStore().InitializeAsync());
        var storeId = (await harness.Store.GetExecutionContextAsync()).StoreId;
        var job = await new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("oracle.resume"), "saved");
        // Model interruption before the first checkpoint, after some DDL already committed.
        await harness.ExecuteAsync("UPDATE dj_schema SET status='Installing',step=0");
        await harness.ExecuteAsync("DROP INDEX ix_dj_jobs_ready");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Store.GetAsync(job));
        await harness.NewStore().InitializeAsync();
        Assert.Equal(storeId, (await harness.Store.GetExecutionContextAsync()).StoreId);
        Assert.Equal("saved", (await harness.Store.GetAsync(job))!.Payload.GetString());
        await harness.ExecuteAsync("DELETE FROM dj_admission");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("oracle.refused"), "refused"));
        Assert.Single(await harness.Store.ListAsync(new()));
    }

    [OracleTheory, MemberData(nameof(Databases))]
    public async Task Select_only_credential_reads_empty_tables_and_capacity_but_cannot_mutate(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("oracle.permissions"), "saved");
        await harness.ProvisionReadOnlyOracleAsync();
        var reader = new OracleJobStore(harness.ReadOnlyConnectionString!, new(Schema: harness.OracleSchema));
        Assert.Equal("NoRecentCapableWorker", (await reader.ReadReadinessAsync(id)).Reason);
        Assert.Equal(0, (await reader.ReadRuntimeMetricsAsync(new())).FinishedAttempts);
        Assert.Null((await reader.ReadRetentionStatusAsync()).Configuration);
        Assert.Empty((await reader.AttemptsAsync(id)));
        await using var connection = new OracleConnection(harness.ReadOnlyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {harness.OracleSchema}.dj_jobs SET status='Succeeded'";
        var denied = await Assert.ThrowsAsync<OracleException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains(denied.Number, new[] { 1031, 41900 });
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(id))!.Status);
    }

    [OracleTheory, MemberData(nameof(Databases))]
    public async Task Dml_runtime_does_not_require_migration_or_ddl_privileges(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.ProvisionReadOnlyOracleAsync();
        var runtimeUser = new OracleConnectionStringBuilder(harness.ReadOnlyConnectionString!).UserID;
        await using var admin = new OracleConnection(Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE"));
        await admin.OpenAsync();
        await using var grant = admin.CreateCommand();
        grant.CommandText = $"BEGIN FOR t IN (SELECT table_name FROM all_tables WHERE owner='{harness.OracleSchema}') LOOP EXECUTE IMMEDIATE 'GRANT INSERT,UPDATE,DELETE ON {harness.OracleSchema}.' || t.table_name || ' TO {runtimeUser}'; END LOOP; END;";
        await grant.ExecuteNonQueryAsync();
        var store = new OracleJobStore(harness.ReadOnlyConnectionString!, new(Schema: harness.OracleSchema));
        var definition = new JobDefinition<string>("oracle.dml-only");
        var id = await new JobClient(store).EnqueueAsync(definition, "saved");
        var job = (await store.ClaimAsync(new("runtime", [definition.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        Assert.Equal(id, job.Id);
        Assert.True(await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        var denied = await Assert.ThrowsAsync<OracleException>(() => store.InitializeAsync());
        Assert.Contains("DBMS_LOCK", denied.Message, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(id))!.Status);
    }
}

internal sealed class OracleTheoryAttribute : TheoryAttribute
{
    public OracleTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE")))
        {
            Skip = "Supply DURABLEJOBS_ORACLE for the disposable real-provider qualification.";
        }
    }
}
