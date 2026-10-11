using Xunit;

namespace WorkThrough.Tests;

/// <summary>Keyed concurrency, rate limits, flow parallelism and weighted queue fairness across independent store instances (ADR-018).</summary>
public sealed class AdmissionControlTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Adjust admission policy");

    private static ClaimRequest Claim(string worker = "worker", TimeSpan? lease = null, params string[] queues) =>
        new(worker, [JobContractId.For<Alpha>(), JobContractId.For<Beta>()], queues.Length == 0 ? [JobDefaults.Queue] : queues, lease ?? Lease);

    private static Task<Guid> EnqueueAsync(IJobStore store, string? partition, string queue = JobDefaults.Queue, string value = "work") =>
        new JobClient(store).EnqueueAsync(new Alpha(value), new(Queue: queue, PartitionKey: partition));

    [Theory, MemberData(nameof(Providers))]
    public async Task Partition_limit_admits_one_attempt_per_key_across_independent_workers(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Partition, null, 1)], []);
        for (var i = 0; i < 3; i++)
        {
            await EnqueueAsync(harness.Store, "customer-a");
            await EnqueueAsync(harness.Store, "customer-b");
        }

        await EnqueueAsync(harness.Store, null);
        await EnqueueAsync(harness.Store, null);

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => harness.NewStore().ClaimAsync(Claim($"worker-{i}")))));
        var admitted = claims.OfType<JobRecord>().ToArray();
        Assert.Equal(4, admitted.Length);
        Assert.Single(admitted, job => job.PartitionKey == "customer-a");
        Assert.Single(admitted, job => job.PartitionKey == "customer-b");
        Assert.Equal(2, admitted.Count(job => job.PartitionKey is null));

        var first = admitted.Single(job => job.PartitionKey == "customer-a");
        Assert.True(await harness.Store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal("customer-a", (await harness.Store.ClaimAsync(Claim()))!.PartitionKey);
        Assert.Null(await harness.Store.ClaimAsync(Claim()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Type_scoped_partition_limit_leaves_other_types_unconstrained(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Partition, "tests.alpha", 1)], []);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 2; i++)
        {
            await client.EnqueueAsync(new Alpha("a"), new(PartitionKey: "shared"));
            await client.EnqueueAsync(new Beta("b"), new(PartitionKey: "shared"));
        }

        var admitted = new List<JobRecord>();
        while (await harness.Store.ClaimAsync(Claim()) is { } job)
        {
            admitted.Add(job);
        }

        Assert.Single(admitted, job => job.Contract == JobContractId.For<Alpha>());
        Assert.Equal(2, admitted.Count(job => job.Contract == JobContractId.For<Beta>()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task An_expired_lease_frees_its_partition_slot(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Partition, null, 1)], []);
        await EnqueueAsync(harness.Store, "customer");
        await EnqueueAsync(harness.Store, "customer");
        Assert.NotNull(await harness.Store.ClaimAsync(Claim("crashed", TimeSpan.FromSeconds(1))));
        Assert.Null(await harness.Store.ClaimAsync(Claim("replacement")));

        await WorkerTests.EventuallyAsync(async () => await harness.NewStore().ClaimAsync(Claim("replacement")) is not null);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Rate_limit_counts_starts_across_workers_in_a_sliding_window(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([], [new(null, 2, RateWindow)]);
        var waiting = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            waiting.Add(await EnqueueAsync(harness.Store, null));
        }

        foreach (var worker in new[] { "first", "second" })
        {
            var job = (await harness.NewStore().ClaimAsync(Claim(worker)))!;
            Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
            waiting.Remove(job.Id);
        }

        Assert.Null(await harness.NewStore().ClaimAsync(Claim("third")));
        Assert.Equal(ReadinessReasons.RateLimited, (await harness.Store.ReadReadinessAsync(waiting[0])).Reason);

        await WorkerTests.EventuallyAsync(async () => await harness.NewStore().ClaimAsync(Claim("third")) is not null);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Per_partition_rate_limit_counts_each_key_separately(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([], [new(null, 1, TimeSpan.FromMinutes(1), PerKey: KeyScope.Partition)]);
        await EnqueueAsync(harness.Store, "customer-a");
        await EnqueueAsync(harness.Store, "customer-a");
        await EnqueueAsync(harness.Store, "customer-b");
        await EnqueueAsync(harness.Store, null);

        var admitted = new List<JobRecord>();
        while (await harness.Store.ClaimAsync(Claim()) is { } job)
        {
            admitted.Add(job);
            Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        }

        Assert.Equal(["customer-a", "customer-b", null], admitted.Select(job => job.PartitionKey).OrderBy(key => key ?? "~", StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Readiness_explains_a_full_partition(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Partition, null, 1)], []);
        await EnqueueAsync(harness.Store, "customer");
        await EnqueueAsync(harness.Store, "customer");
        Assert.NotNull(await harness.Store.ClaimAsync(Claim()));
        var waiting = Assert.Single(await harness.Store.ListAsync(new(Status: JobStatus.Queued))).Id;

        var readiness = await harness.Store.ReadReadinessAsync(waiting);
        Assert.Equal(ReadinessReasons.PartitionCapacity, readiness.Reason);
        Assert.Equal(1, readiness.Capacity!.PartitionMaximum);
        Assert.Equal(1, readiness.Capacity.PartitionAdmitted);
        Assert.True(readiness.Capacity.PartitionLimited);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Policy_commands_take_effect_at_the_next_claim_and_are_audited(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        await EnqueueAsync(store, "customer");
        await EnqueueAsync(store, "customer");
        await store.SetKeyedLimitAsync(new(KeyScope.Partition, null, 0), Command()).SucceedsAsync();
        Assert.Null(await store.ClaimAsync(Claim()));

        await store.SetKeyedLimitAsync(new(KeyScope.Partition, null, 1), Command()).SucceedsAsync();
        Assert.NotNull(await store.ClaimAsync(Claim()));
        Assert.Null(await store.ClaimAsync(Claim()));

        await store.RemoveKeyedLimitAsync(KeyScope.Partition, null, Command()).SucceedsAsync();
        await store.RemoveKeyedLimitAsync(KeyScope.Partition, null, Command()).FailsAsync(ErrorType.NotFound);
        Assert.NotNull(await store.ClaimAsync(Claim()));

        await store.SetRateLimitAsync(new("tests.alpha", 5, TimeSpan.FromMinutes(1), PerKey: KeyScope.Partition), Command()).SucceedsAsync();
        await store.SetRateLimitAsync(new(null, 0, TimeSpan.FromMinutes(1)), Command()).FailsAsync(ErrorType.Validation);
        await store.SetKeyedLimitAsync(new(KeyScope.Partition, "NotAKey", 1), Command()).FailsAsync(ErrorType.Validation);
        var policies = await store.AdmissionPoliciesAsync();
        Assert.Empty(policies.KeyedLimits);
        Assert.Equal(new RateLimit("tests.alpha", 5, TimeSpan.FromMinutes(1), PerKey: KeyScope.Partition), Assert.Single(policies.RateLimits));

        await store.RemoveRateLimitAsync("tests.alpha", perKey: null, Command()).FailsAsync(ErrorType.NotFound);
        await store.RemoveRateLimitAsync("tests.alpha", perKey: KeyScope.Partition, Command()).SucceedsAsync();
        Assert.Empty((await store.AdmissionPoliciesAsync()).RateLimits);
        string[] applied = [AuditActions.SetKeyedLimit, AuditActions.SetKeyedLimit, AuditActions.RemoveKeyedLimit,
            AuditActions.SetRateLimit, AuditActions.RemoveRateLimit];

        // Rejected commands commit no audit entry.
        Assert.Equal(applied.Order(StringComparer.Ordinal), (await store.AuditAsync()).Select(entry => entry.Action).Order(StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Tenant_limits_bound_each_tenant_independently_of_its_partitions(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Tenant, null, 2), new(KeyScope.Partition, null, 1)], []);
        var client = new JobClient(harness.Store);
        foreach (var partition in new[] { "invoice-1", "invoice-1", "invoice-2", "invoice-3" })
        {
            await client.EnqueueAsync(new Alpha("busy"), new(TenantId: "tenant-busy", PartitionKey: partition));
        }

        // Partition keys are global across tenants; tenants that reuse business IDs namespace them.
        await client.EnqueueAsync(new Alpha("quiet"), new(TenantId: "tenant-quiet", PartitionKey: "tenant-quiet/invoice-1"));
        await client.EnqueueAsync(new Alpha("quiet"), new(TenantId: "tenant-quiet"));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => harness.NewStore().ClaimAsync(Claim($"worker-{i}")))));
        var admitted = claims.OfType<JobRecord>().ToArray();

        // The busy tenant gets its two slots on distinct partitions; its backlog leaves the quiet tenant's slots free.
        Assert.Equal(2, admitted.Count(job => job.TenantId == "tenant-busy"));
        Assert.Equal(2, admitted.Where(job => job.TenantId == "tenant-busy").Select(job => job.PartitionKey).Distinct().Count());
        Assert.Equal(2, admitted.Count(job => job.TenantId == "tenant-quiet"));

        var waiting = (await harness.Store.ListAsync(new(Status: JobStatus.Queued))).Where(job => job.TenantId == "tenant-busy")
            .Single(job => admitted.All(running => running.PartitionKey != job.PartitionKey));
        var readiness = await harness.Store.ReadReadinessAsync(waiting.Id);
        Assert.Equal(ReadinessReasons.TenantCapacity, readiness.Reason);
        Assert.Equal((2, 2L, true), (readiness.Capacity!.TenantMaximum, readiness.Capacity.TenantAdmitted, readiness.Capacity.TenantLimited));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Per_tenant_rate_limit_counts_each_tenant_separately(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedAdmissionPoliciesAsync([], [new(null, 1, TimeSpan.FromMinutes(1), PerKey: KeyScope.Tenant)]);
        var client = new JobClient(harness.Store);
        await client.EnqueueAsync(new Alpha("a"), new(TenantId: "tenant-a", PartitionKey: "one"));
        await client.EnqueueAsync(new Alpha("a"), new(TenantId: "tenant-a", PartitionKey: "two"));
        await client.EnqueueAsync(new Alpha("b"), new(TenantId: "tenant-b"));
        await client.EnqueueAsync(new Alpha("none"));

        var admitted = new List<JobRecord>();
        while (await harness.Store.ClaimAsync(Claim()) is { } job)
        {
            admitted.Add(job);
            Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        }

        Assert.Equal(["tenant-a", "tenant-b", null], admitted.Select(job => job.TenantId).OrderBy(key => key ?? "~", StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Max_parallel_steps_releases_a_fan_out_gradually(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var builder = new FlowBuilder("tests.fanout", 1);
        for (var i = 0; i < 5; i++)
        {
            builder.Step<Alpha>($"branch-{i}", new Alpha($"branch-{i}"));
        }

        var flow = builder.Build();
        var bounded = await new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(MaxParallelSteps: 2));
        var unbounded = await new WorkflowClient(harness.Store).StartAsync(flow, new { });

        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => harness.NewStore().ClaimAsync(Claim($"worker-{i}")))));
        var admitted = claims.OfType<JobRecord>().ToArray();
        Assert.Equal(2, admitted.Count(job => job.WorkflowRunId == bounded));
        Assert.Equal(5, admitted.Count(job => job.WorkflowRunId == unbounded));

        var waiting = (await harness.Store.ListFlowJobsAsync(bounded)).First(job => job.Status == JobStatus.Queued);
        var readiness = await harness.Store.ReadReadinessAsync(waiting.Id);
        Assert.Equal(ReadinessReasons.FlowParallelism, readiness.Reason);
        Assert.Equal((2, 2L), (readiness.Capacity!.FlowMaximum, readiness.Capacity.FlowAdmitted));

        var finished = admitted.First(job => job.WorkflowRunId == bounded);
        Assert.True(await harness.Store.CompleteAsync(finished.Id, finished.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(bounded, (await harness.Store.ClaimAsync(Claim()))!.WorkflowRunId);
        Assert.Null(await harness.Store.ClaimAsync(Claim()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(MaxParallelSteps: 0)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_backlog_in_one_queue_does_not_delay_another_queue(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        for (var i = 0; i < 8; i++)
        {
            await EnqueueAsync(harness.Store, null, "bulk");
        }

        await EnqueueAsync(harness.Store, null, "urgent");
        await EnqueueAsync(harness.Store, null, "urgent");

        var served = await DrainAsync(harness.Store, 4, "bulk", "urgent");
        Assert.Equal(2, served.Count(queue => queue == "urgent"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Queue_weights_divide_claims_between_competing_queues(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SetQueueWeightAsync("bulk", 3, 0, Command()).SucceedsAsync();
        for (var i = 0; i < 9; i++)
        {
            await EnqueueAsync(harness.Store, null, "bulk");
            await EnqueueAsync(harness.Store, null, "urgent");
        }

        var served = await DrainAsync(harness.Store, 8, "bulk", "urgent");
        Assert.Equal(6, served.Count(queue => queue == "bulk"));
        Assert.Equal(3, Assert.Single(await harness.Store.QueuesAsync(), queue => queue.Queue == "bulk").Weight);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_queue_that_was_idle_gains_no_burst_credit(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        for (var i = 0; i < 10; i++)
        {
            await EnqueueAsync(harness.Store, null, "steady");
        }

        await DrainAsync(harness.Store, 5, "steady", "sporadic");
        for (var i = 0; i < 4; i++)
        {
            await EnqueueAsync(harness.Store, null, "sporadic");
        }

        var served = await DrainAsync(harness.Store, 4, "steady", "sporadic");
        Assert.Equal(2, served.Count(queue => queue == "sporadic"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Flow_runs_carry_their_partition_key_and_tenant(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.partitioned", 1).Step<Alpha>("first", new Alpha("a")).Step<Beta>("second", new Beta("b")).After("first").Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(PartitionKey: "customer", TenantId: "tenant"));
        Assert.All(await harness.Store.ListFlowJobsAsync(run), job => Assert.Equal(("customer", "tenant"), (job.PartitionKey, job.TenantId)));
        await Assert.ThrowsAsync<ArgumentException>(() => new JobClient(harness.Store).EnqueueAsync(new Alpha("a"), new(TenantId: "tenant ")));
        await Assert.ThrowsAsync<ArgumentException>(() => new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(PartitionKey: " padded")));
        await Assert.ThrowsAsync<ArgumentException>(() => new JobClient(harness.Store).EnqueueAsync(new Alpha("a"), new(PartitionKey: "")));

        var page = await harness.Store.SearchExecutionsAsync(new(PartitionKey: "customer"));
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(("customer", "tenant"), (item.PartitionKey, item.TenantId)));
        Assert.Equal(2, (await harness.Store.SearchExecutionsAsync(new(TenantId: "tenant"))).Items.Count);
        Assert.Empty((await harness.Store.SearchExecutionsAsync(new(TenantId: "other"))).Items);
    }

    /// <summary>Claims and completes up to <paramref name="count"/> jobs, returning the queue of each in claim order.</summary>
    private static async Task<List<string>> DrainAsync(IJobStore store, int count, params string[] queues)
    {
        var served = new List<string>();
        while (served.Count < count && await store.ClaimAsync(Claim("fair", null, queues)) is { } job)
        {
            served.Add(job.Queue);
            Assert.True(await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        }

        return served;
    }
}
