using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>
/// Receipts stored before fingerprints were built without reflection must still match: the same enqueue, flow start or operator
/// command must hash exactly as the anonymous objects it replaced did.
/// </summary>
public sealed class FingerprintCompatibilityTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly OperationActor Actor = new("https://login.example", "operator-1", "Ada");

    [Theory]
    [InlineData(null, null)]
    [InlineData("customer-7", null)]
    [InlineData("customer-7", "tenant-3")]
    public void Job_receipts_hash_as_before(string? partitionKey, string? tenantId)
    {
        var job = new JobRecord(Guid.NewGuid(), new("orders.reserve", 2), JsonDocument.Parse("""{"orderId":"o-1","lines":[1,2]}""").RootElement,
            "orders", "corr-9", JobStatus.Queued, 0, 5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "key-1",
            ExpiresAt: new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), CausationJobId: Guid.NewGuid())
        {
            PartitionKey = partitionKey,
            TenantId = tenantId
        };
        object previous = (partitionKey, tenantId) switch
        {
            (null, null) => new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId },
            (_, null) => new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId, job.PartitionKey },
            _ => new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId, job.PartitionKey, job.TenantId }
        };

        Assert.Equal(Previous(previous), (string)Method("JobFingerprint").Invoke(null, [job])!);
    }

    [Fact]
    public void Flow_start_receipts_hash_as_before()
    {
        var definition = new FlowBuilder("orders.fulfil", 1).Step("reserve", new JobContractId("orders.reserve", 1), new { quantity = 3 }).Build();
        var input = JsonDocument.Parse("""{"orderId":"o-1"}""").RootElement;

        Assert.Equal(Previous(new { definition, input, correlationId = "corr-1", partitionKey = "p", tenantId = (string?)null, maxParallelSteps = (int?)4 }),
            Current(("definition", definition), ("input", input), ("correlationId", "corr-1"), ("partitionKey", "p"), ("tenantId", null),
                ("maxParallelSteps", 4)));
    }

    [Fact]
    public void Operator_commands_hash_as_before()
    {
        var schedule = ScheduleDefinition.Every("stock.count", TimeSpan.FromMinutes(5), new JobDefinition<string>("stock.count"), "warehouse-1");
        var limit = new ConcurrencyLimit("orders.reserve", 4);

        Assert.Equal(Previous(new
        {
            Actor,
            Reason = "edit",
            action = "schedule.edit",
            target = "stock.count@1",
            arguments = new { definition = schedule, expectedRevision = (long?)3 }
        }),
            Current(("actor", Actor), ("reason", "edit"), ("action", "schedule.edit"), ("target", "stock.count@1"),
                ("arguments", Args(("definition", schedule), ("expectedRevision", 3L)))));
        Assert.Equal(Previous(new { Actor, Reason = "limit", action = "limit.change", target = "orders.reserve", arguments = limit }),
            Current(("actor", Actor), ("reason", "limit"), ("action", "limit.change"), ("target", "orders.reserve"),
                ("arguments", Method("Arguments").Invoke(null, [limit]))));
        var through = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        Assert.Equal(Previous(new { Actor, Reason = "trim", action = "events.trim", target = "event journal", arguments = new { through } }),
            Current(("actor", Actor), ("reason", "trim"), ("action", "events.trim"), ("target", "event journal"), ("arguments", Args(("through", through)))));
    }

    private static string Previous(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JobJson.Options))));

    private static string Current(params (string Name, object? Value)[] properties) => (string)Method("Fingerprint").Invoke(null, [Args(properties)])!;

    private static object Args(params (string Name, object? Value)[] properties) => Method("Args").Invoke(null, [properties])!;

    private static MethodInfo Method(string name) => typeof(RelationalJobStore).GetMethod(name, Private)!;
}
