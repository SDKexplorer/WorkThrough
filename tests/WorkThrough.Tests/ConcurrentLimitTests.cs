using Xunit;

namespace WorkThrough.Tests;

/// <summary>
/// Claims under shared limits racing from independent stores (ADR-022). From engine protocol 3 they lock limit rows instead of the
/// admission lock; at protocol 2 they serialize as before. Both must admit exactly the limits.
/// </summary>
public sealed class ConcurrentLimitTests
{
    private const int Workers = 8;
    private const int Rounds = 6;

    public static IEnumerable<object[]> Cases() => StorageConformanceTests.RowLockProviders()
        .SelectMany(provider => new[] { EngineProtocol.FanOut, EngineProtocol.ConcurrentLimits }.Select(protocol => new object[] { provider[0], protocol }));

    private static ClaimRequest Claim(string worker) =>
        new(worker, [JobContractId.For<Alpha>(), JobContractId.For<Beta>()], [JobDefaults.Queue], TimeSpan.FromMinutes(1)) { MaximumJobs = 5 };

    [Theory, MemberData(nameof(Cases))]
    public async Task Tenant_partition_and_type_limits_hold_while_claims_race(string provider, int protocol)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await SetProtocolAsync(harness, protocol);
        await harness.Store.SeedLimitsAsync([new("tests.beta", 3)]);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Tenant, null, 2), new(KeyScope.Partition, null, 1)], []);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 60; i++)
        {
            await client.EnqueueAsync(new Alpha($"a{i}"), new(TenantId: $"tenant-{i % 5}", PartitionKey: $"customer-{i % 7}"));
            await client.EnqueueAsync(new Beta($"b{i}"), new(TenantId: $"tenant-{i % 5}"));
        }

        var leased = await RaceAsync(harness);

        Assert.All(leased.GroupBy(job => job.TenantId), tenant => Assert.True(tenant.Count() <= 2, $"{tenant.Key} ran {tenant.Count()}"));
        Assert.All(leased.Where(job => job.PartitionKey is not null).GroupBy(job => job.PartitionKey), key => Assert.Single(key));
        Assert.True(leased.Count(job => job.Contract.Key == "tests.beta") <= 3);
        Assert.True(leased.Count <= 10);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Fleet_rate_and_flow_limits_hold_while_claims_race(string provider, int protocol)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await SetProtocolAsync(harness, protocol);
        await harness.Store.SeedLimitsAsync([new(null, 5)]);
        await harness.Store.SeedAdmissionPoliciesAsync([], [new("tests.alpha", 4, TimeSpan.FromMinutes(1))]);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 20; i++)
        {
            await client.EnqueueAsync(new Alpha($"a{i}"));
        }

        var flow = new FlowBuilder("tests.bounded", 1).Step<Beta>("one", new Beta("1")).Step<Beta>("two", new Beta("2")).Step<Beta>("three", new Beta("3")).Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(MaxParallelSteps: 2));

        var leased = await RaceAsync(harness);

        // Four starts fit the rate and two steps fit the flow, but the fleet admits only five of those six.
        Assert.Equal(5, leased.Count);
        Assert.True(leased.Count(job => job.Contract.Key == "tests.alpha") <= 4);
        Assert.True(leased.Count(job => job.WorkflowRunId == run) <= 2);
    }

    /// <summary>Claims from many stores at once, never completing, so every lease counts against the limits until the end.</summary>
    private static async Task<List<JobRecord>> RaceAsync(StoreHarness harness)
    {
        var leased = new System.Collections.Concurrent.ConcurrentBag<JobRecord>();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var store = harness.NewStore();
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var job in await store.ClaimBatchAsync(Claim($"worker-{worker}")))
                {
                    leased.Add(job);
                }
            }
        })));
        Assert.Equal(leased.Count, leased.Select(job => job.Id).Distinct().Count());

        return [.. leased];
    }

    private static Task SetProtocolAsync(StoreHarness harness, int protocol) =>
        RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={protocol}");
}
