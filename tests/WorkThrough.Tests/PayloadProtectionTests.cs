using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>Business data encrypted at rest (ADR-027): stores encrypt on write and decrypt on read.</summary>
public sealed class PayloadProtectionTests
{
    private const string Secret = "card-4242";
    private static readonly SignalDefinition<Beta> Approval = new("approve");

    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static readonly byte[] OldKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] NewKey = RandomNumberGenerator.GetBytes(32);

    private static AesGcmPayloadProtector Protector(string current = "k1") =>
        new(new Dictionary<string, byte[]> { ["k1"] = OldKey, ["k2"] = NewKey }, current);

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    [Theory, MemberData(nameof(Providers))]
    public async Task Inputs_outputs_flow_inputs_and_signals_are_stored_encrypted_and_read_back_in_plain(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        store.PayloadProtector = Protector();
        var client = new JobClient(store);
        var id = await client.EnqueueAsync(new Alpha(Secret), new(IdempotencyKey: "pay-once"));
        Assert.Equal(id, await client.EnqueueAsync(new Alpha(Secret), new(IdempotencyKey: "pay-once")));

        var claimed = (await store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal(Secret, claimed.Payload.GetProperty("value").GetString());
        Assert.True(await store.CompleteAsync(claimed.Id, claimed.LeaseToken!.Value, JobResult.From(new Beta(Secret))));
        Assert.Equal(Secret, (await store.GetAsync(id))!.Output!.Value.GetProperty("value").GetString());

        var flow = new FlowBuilder("tests.protection", 1).WaitForSignal("approval", Approval, TimeSpan.FromHours(1)).Build();
        var flows = new WorkflowClient(store);
        var run = await flows.StartAsync(flow, new Alpha(Secret));
        Assert.True(await flows.SignalAsync(run, Approval, new Beta(Secret), "approval"));
        await store.DispatchSignalsAsync();
        Assert.Equal(FlowStatus.Succeeded, (await store.GetWorkflowAsync(run))!.Status);

        string[] stored =
        [
            .. await ReadAsync(harness, "SELECT payload FROM dj_jobs"), .. await ReadAsync(harness, "SELECT output FROM dj_jobs WHERE output IS NOT NULL"),
            .. await ReadAsync(harness, "SELECT input FROM dj_flows"), .. await ReadAsync(harness, "SELECT payload FROM dj_signals")
        ];
        Assert.Equal(6, stored.Length);
        Assert.All(stored, value =>
        {
            Assert.DoesNotContain(Secret, value);
            Assert.Equal("k1", PayloadEnvelope.KeyOf(JsonDocument.Parse(value).RootElement));
        });
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_rotated_key_writes_new_work_and_still_reads_old_work(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var before = harness.Store;
        before.PayloadProtector = Protector("k1");
        var old = await new JobClient(before).EnqueueAsync(new Alpha("old"));

        var after = harness.Store;
        after.PayloadProtector = Protector("k2");
        var fresh = await new JobClient(after).EnqueueAsync(new Alpha("new"));

        Assert.Equal("old", (await after.GetAsync(old))!.Payload.GetProperty("value").GetString());
        Assert.Equal("new", (await after.GetAsync(fresh))!.Payload.GetProperty("value").GetString());
        var keys = (await ReadAsync(harness, "SELECT payload FROM dj_jobs")).Select(value => PayloadEnvelope.KeyOf(JsonDocument.Parse(value).RootElement));
        Assert.Equal(["k1", "k2"], keys.Order());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Nothing_is_encrypted_until_every_worker_supports_protection(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.Loops}");
        var store = harness.Store;
        store.PayloadProtector = Protector();
        await new JobClient(store).EnqueueAsync(new Alpha(Secret));

        Assert.Contains(Secret, Assert.Single(await ReadAsync(harness, "SELECT payload FROM dj_jobs")));
    }

    [Fact]
    public async Task A_worker_without_the_key_retries_the_job_instead_of_running_or_rejecting_it()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddJob<Greet, GreetJob>().ProtectPayloads("k1",
            new Dictionary<string, byte[]> { ["k1"] = OldKey }), services => services.AddSingleton<TestHostTests.Attempts>());
        var foreign = new AesGcmPayloadProtector(new Dictionary<string, byte[]> { ["elsewhere"] = NewKey }, "elsewhere");
        var id = await host.Jobs.EnqueueAsync(new Greet("Ada"));
        var job = (await host.WaitForJobAsync(id)).Output!.Value.GetProperty("text").GetString();
        Assert.Equal("Hello Ada", job);

        var unreadable = await host.Store.EnqueueAsync(new JobRecord(Guid.NewGuid(), JobContractId.For<Greet>(),
            foreign.Protect(JsonSerializer.SerializeToElement(new Greet("Grace"), JobJson.Options)), JobDefaults.Queue, null, JobStatus.Queued, 0, 3,
            host.UtcNow, host.UtcNow));
        using var timeout = new CancellationTokenSource(WorkThroughTestHost.DefaultTimeout);
        while (await host.Queries.GetAsync(unreadable) is not { Attempt: > 0, Status: JobStatus.Queued })
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }


        var stored = (await host.Queries.GetAsync(unreadable))!;
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Contains("elsewhere", stored.Error);
        Assert.Equal(AttemptStatus.Failed, Assert.Single(await host.Queries.AttemptsAsync(unreadable)).Status);
    }

    [Fact]
    public void An_envelope_cannot_be_relabelled_to_another_key()
    {
        var protector = Protector();
        var envelope = protector.Protect(JsonSerializer.SerializeToElement(new Alpha(Secret), JobJson.Options));
        var relabelled = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
        {
            [PayloadEnvelope.MarkerProperty] = PayloadEnvelope.Algorithm,
            [PayloadEnvelope.KeyProperty] = "k2",
            [PayloadEnvelope.NonceProperty] = envelope.GetProperty(PayloadEnvelope.NonceProperty).GetString(),
            [PayloadEnvelope.DataProperty] = envelope.GetProperty(PayloadEnvelope.DataProperty).GetString()
        });

        Assert.Equal(Secret, protector.Unprotect(envelope).GetProperty("value").GetString());
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(relabelled));
        Assert.True(PayloadEnvelope.IsProtected(new AesGcmPayloadProtector(new Dictionary<string, byte[]> { ["k3"] = NewKey }, "k3").Unprotect(envelope)));
    }

    private static async Task<List<string>> ReadAsync(StoreHarness harness, string sql)
    {
        await using var connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(Convert.ToString(reader.GetValue(0))!);
        }

        return values;
    }
}
