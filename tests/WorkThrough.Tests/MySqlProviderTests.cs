using MySqlConnector;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class MySqlProviderTests
{
    private const int CommandDenied = 1142;

    public static IEnumerable<object[]> Databases() =>
        StorageConformanceTests.Providers().Where(provider => (string)provider[0] is "mysql" or "mariadb");

    [Theory]
    [InlineData("Server=localhost")]
    [InlineData("Server=localhost;Database=mysql")]
    [InlineData("Server=localhost;Database=information_schema")]
    public void A_dedicated_database_is_required(string connectionString) =>
        Assert.Throws<ArgumentException>(() => new MySqlJobStore(connectionString));

    [MySqlTheory, MemberData(nameof(Databases))]
    public async Task Concurrent_initializers_and_resuming_a_ddl_checkpoint_preserve_identity(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider, initialize: false);
        await Task.WhenAll(harness.NewStore().InitializeAsync(), harness.NewStore().InitializeAsync());
        var storeId = (await harness.Store.GetExecutionContextAsync()).StoreId;
        var job = await new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("mysql.resume"), "saved");

        // Model an interruption before the first checkpoint, after some DDL already committed and some did not run.
        await harness.ExecuteAsync("UPDATE dj_schema SET status='Installing',step=0");
        await harness.ExecuteAsync("DROP TABLE dj_maintenance_batches");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Store.GetAsync(job));
        await harness.NewStore().InitializeAsync();
        Assert.Equal(storeId, (await harness.Store.GetExecutionContextAsync()).StoreId);
        Assert.Equal("saved", (await harness.Store.GetAsync(job))!.Payload.GetString());

        await harness.ExecuteAsync("DELETE FROM dj_admission");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("mysql.refused"), "refused"));
        Assert.Single(await harness.Store.ListAsync(new()));
    }

    [MySqlTheory, MemberData(nameof(Databases))]
    public async Task A_select_only_credential_reads_but_cannot_mutate(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new JobDefinition<string>("mysql.permissions"), "saved");
        await harness.ProvisionReadOnlyMySqlAsync();
        var reader = new MySqlJobStore(harness.ReadOnlyConnectionString!);
        Assert.Equal("NoRecentCapableWorker", (await reader.ReadReadinessAsync(id)).Reason);
        Assert.Equal(0, (await reader.ReadRuntimeMetricsAsync(new())).FinishedAttempts);
        Assert.Empty(await reader.AttemptsAsync(id));

        await using var connection = new MySqlConnection(harness.ReadOnlyConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("UPDATE dj_jobs SET status='Succeeded'", connection);
        Assert.Equal(CommandDenied, (await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync())).Number);
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(id))!.Status);
    }

    [MySqlTheory, MemberData(nameof(Databases))]
    public async Task A_dml_runtime_credential_runs_work_without_ddl_privileges(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.ProvisionReadOnlyMySqlAsync();
        var settings = new MySqlConnectionStringBuilder(harness.ReadOnlyConnectionString!);
        await using (var admin = new MySqlConnection(Environment.GetEnvironmentVariable(provider == "mysql" ? "DURABLEJOBS_MYSQL" : "DURABLEJOBS_MARIADB")))
        {
            await admin.OpenAsync();
            await using var grant = new MySqlCommand($"GRANT INSERT,UPDATE,DELETE ON {settings.Database}.* TO '{settings.UserID}'@'%'", admin);
            await grant.ExecuteNonQueryAsync();
        }

        var store = new MySqlJobStore(settings.ConnectionString);
        var definition = new JobDefinition<string>("mysql.dml-only");
        var id = await new JobClient(store).EnqueueAsync(definition, "saved");
        var job = (await store.ClaimAsync(new("runtime", [definition.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        Assert.Equal(id, job.Id);
        Assert.True(await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        await Assert.ThrowsAsync<MySqlException>(() => store.InitializeAsync());
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(id))!.Status);
    }
}

internal sealed class MySqlTheoryAttribute : TheoryAttribute
{
    public MySqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_MYSQL"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_MARIADB")))
        {
            Skip = "Supply DURABLEJOBS_MYSQL or DURABLEJOBS_MARIADB for the disposable real-provider qualification.";
        }
    }
}
