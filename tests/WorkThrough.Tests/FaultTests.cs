using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace WorkThrough.Tests;

public sealed class FaultTests
{
    private static readonly JobDefinition<Alpha> Definition = new("tests.alpha");
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task Lost_destination_commit_acknowledgment_retries_without_duplicate_job(string provider)
    {
        await using var source = await StoreHarness.CreateAsync(provider);
        await using var destination = await StoreHarness.CreateAsync(provider);
        var receipt = await source.Store.InTransactionAsync((_, _, jobs, ct) => jobs.SendAsync(Definition, new Alpha("outbox"), cancellationToken: ct));
        await Assert.ThrowsAsync<IOException>(() => source.Store.DispatchOutboxAsync(new LoseAcknowledgment(destination.Store)));
        Assert.Single(await destination.Store.ListAsync(new()));
        Assert.Equal("Pending", (await source.Store.OutboxDeliveryAsync(receipt))!.Status);
        Assert.Equal(1, await source.NewStore().DispatchOutboxAsync(destination.Store));
        Assert.Single(await destination.Store.ListAsync(new()));
        Assert.Equal("Delivered", (await source.Store.OutboxDeliveryAsync(receipt))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Broken_connection_rolls_back_accepted_work_and_a_new_connection_can_continue(string provider)
    {
        await using var database = await StoreHarness.CreateAsync(provider);
        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.InTransactionAsync(async (connection, transaction, jobs, ct) =>
        {
            await jobs.EnqueueAsync(Definition, new Alpha("uncommitted"), cancellationToken: ct);
            if (provider == "postgres")
            {
                await using var id = connection.CreateCommand();
                id.Transaction = transaction;
                id.CommandText = "SELECT pg_backend_pid()";
                var pid = (int)(await id.ExecuteScalarAsync(ct))!;
                await using var killer = new NpgsqlConnection(database.ConnectionString);
                await killer.OpenAsync(ct);
                await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", killer);
                kill.Parameters.AddWithValue("pid", pid);
                Assert.True((bool)(await kill.ExecuteScalarAsync(ct))!);
            }
            else if (provider == "oracle")
            {
                await using var id = connection.CreateCommand();
                id.Transaction = transaction;
                id.CommandText = "SELECT SYS_CONTEXT('USERENV','SID') FROM dual";
                var sid = Convert.ToInt32(await id.ExecuteScalarAsync(ct));
                await using var killer = new OracleConnection(Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE"));
                await killer.OpenAsync(ct);
                await using var kill = killer.CreateCommand();
                kill.BindByName = true;
                kill.CommandText = "SELECT serial# FROM v$session WHERE sid=:sid AND username=:owner";
                kill.Parameters.Add("sid", OracleDbType.Int32).Value = sid;
                kill.Parameters.Add("owner", OracleDbType.Varchar2).Value = database.OracleSchema;
                var serial = Convert.ToInt32(await kill.ExecuteScalarAsync(ct));
                kill.Parameters.Clear();
                kill.CommandText = $"ALTER SYSTEM DISCONNECT SESSION '{sid},{serial}' IMMEDIATE";
                await kill.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await connection.CloseAsync();
            }

            return true;
        }));
        Assert.Empty(await database.Store.ListAsync(new()));
        var accepted = await new JobClient(database.NewStore()).EnqueueAsync(Definition, new Alpha("healthy"));
        Assert.NotNull(await database.Store.GetAsync(accepted));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Signal_archive_retains_evidence_and_request_receipts(string provider)
    {
        await using var database = await StoreHarness.CreateAsync(provider);
        var signal = new SignalDefinition<Alpha>("tests.confirm");
        var flow = new FlowBuilder("tests.signal-archive", 1).WaitForSignal("confirm", signal, TimeSpan.FromMinutes(1)).Build();
        var client = new WorkflowClient(database.Store);
        var id = await client.StartAsync(flow, new
        {
        });
        await client.SignalAsync(id, signal, new Alpha("confirmed"), "receipt");
        await database.Store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddMinutes(1)), new(Guid.NewGuid(), new("tests", "archive"), "Archive completed signal flow")).SucceedsAsync();
        var archive = (await database.Store.ExportArchiveAsync(id))!;
        var evidence = System.Text.Json.JsonSerializer.Deserialize<ExecutionArchive>(archive.Json, JobJson.Options)!;
        Assert.Equal("confirmed", Assert.Single(evidence.Signals).Payload.GetProperty("value").GetString());
        Assert.True(await client.SignalAsync(id, signal, new Alpha("confirmed"), "receipt"));
    }

    private sealed class LoseAcknowledgment(IJobStore inner) : DelegatingJobStore(inner)
    {
        public override async Task<Guid> EnqueueAsync(JobRecord job, CancellationToken ct = default)
        {
            await base.EnqueueAsync(job, ct);
            throw new IOException("Injected loss of response after the destination committed.");
        }
    }
}
