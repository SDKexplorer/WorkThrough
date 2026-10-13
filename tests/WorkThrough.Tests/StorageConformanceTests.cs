using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

[JobContract("tests.alpha")]
public sealed record Alpha(string Value);
[JobContract("tests.beta")]
public sealed record Beta(string Value);

public sealed class StorageConformanceTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return ["sqlite"];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE")))
        {
            yield return ["oracle"];
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER")))
        {
            yield return ["sqlserver"];
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES")))
        {
            yield return ["postgres"];
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_MYSQL")))
        {
            yield return ["mysql"];
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DURABLEJOBS_MARIADB")))
        {
            yield return ["mariadb"];
        }
    }

    /// <summary>
    /// Providers for tests of claims and settlements outside the admission lock. SQL Server takes that path only with
    /// READ_COMMITTED_SNAPSHOT on, so it runs both ways.
    /// </summary>
    public static IEnumerable<object[]> RowLockProviders()
    {
        foreach (var provider in Providers())
        {
            yield return provider;
            if ((string)provider[0] == "sqlserver")
            {
                yield return [StoreHarness.SqlServerVersionedReads];
            }
        }
    }

    private static ClaimRequest Claim(string worker = "worker", TimeSpan? lease = null) =>
        new(worker, [JobContractId.For<Alpha>(), JobContractId.For<Beta>()], ["default"], lease ?? TimeSpan.FromSeconds(30));

    /// <summary>Plain job reads ran outside a snapshot and were chosen as deadlock victims by SQL Server under settlement load.</summary>
    [Theory, MemberData(nameof(Providers))]
    public async Task A_completion_batch_settles_each_attempt_under_its_own_lease(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 3; i++)
        {
            await client.EnqueueAsync(new Alpha($"batch-{i}"));
        }

        var claimed = await harness.Store.ClaimBatchAsync(new ClaimRequest("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1)) { MaximumJobs = 3 });
        Assert.Equal(3, claimed.Count);
        var settled = await harness.Store.CompleteBatchAsync([
            new(claimed[0].Id, claimed[0].LeaseToken!.Value, JobResult.Done()),
            new(claimed[1].Id, Guid.NewGuid(), JobResult.Done()),
            new(claimed[2].Id, claimed[2].LeaseToken!.Value, JobResult.From("output"))
        ]);

        Assert.Equal([true, false, true], settled);
        Assert.Equal(JobStatus.Succeeded, (await harness.Store.GetAsync(claimed[0].Id))!.Status);
        Assert.Equal(JobStatus.Running, (await harness.Store.GetAsync(claimed[1].Id))!.Status);
        Assert.Equal("output", (await harness.Store.GetAsync(claimed[2].Id))!.Output!.Value.GetString());
    }

    /// <summary>Server providers settle without the admission lock; the flow row lock must still let exactly one settlement see both branches done.</summary>
    [Theory, MemberData(nameof(RowLockProviders))]
    public async Task Concurrent_settlements_of_parallel_branches_activate_each_join_once(string provider)
    {
        const int runs = 40;
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.parallel-join", 1)
            .Step<Alpha>("left", new Alpha("l")).Step<Alpha>("right", new Alpha("r")).Step<Beta>("join", new Beta("j")).After("left", "right").Build();
        var flows = new WorkflowClient(harness.Store);
        var started = new List<Guid>();
        for (var i = 0; i < runs; i++)
        {
            started.Add(await flows.StartAsync(flow, new { }));
        }

        var branches = await harness.Store.ClaimBatchAsync(new ClaimRequest("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))
        {
            MaximumJobs = runs * 2
        });
        Assert.Equal(runs * 2, branches.Count);

        // Batches mix branches of many flows in different orders, as completion batches from several workers would.
        var batches = branches.OrderBy(_ => Random.Shared.Next()).Chunk(5).ToArray();
        await Task.WhenAll(batches.Select(batch => Task.Run(async () =>
        {
            var settled = await harness.NewStore().CompleteBatchAsync(
                batch.Select(job => new AttemptCompletion(job.Id, job.LeaseToken!.Value, JobResult.Done())).ToArray());
            Assert.All(settled, Assert.True);
        })));

        var joins = await harness.Store.ClaimBatchAsync(new ClaimRequest("worker", [JobContractId.For<Beta>()], ["default"], TimeSpan.FromMinutes(1))
        {
            MaximumJobs = runs * 2
        });
        Assert.Equal(started.Order(), joins.Select(job => job.WorkflowRunId!.Value).Order());
        await AssertJournalIsGapFreeAsync(harness.Store);
    }

    /// <summary>Without limits, claims run outside the admission lock; skipped row locks must still give every job to exactly one claim.</summary>
    [Theory, MemberData(nameof(RowLockProviders))]
    public async Task Concurrent_unrestricted_claims_lease_each_job_once(string provider)
    {
        const int jobs = 200;
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < jobs; i++)
        {
            await client.EnqueueAsync(new Alpha($"job-{i}"));
        }

        var leased = new System.Collections.Concurrent.ConcurrentBag<JobRecord>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(async () =>
        {
            var store = harness.NewStore();
            for (var round = 0; round < jobs; round++)
            {
                var batch = await store.ClaimBatchAsync(new ClaimRequest($"worker-{worker}", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))
                {
                    MaximumJobs = 7
                });
                foreach (var job in batch)
                {
                    leased.Add(job);
                }

                if (batch.Count == 0 && (await store.StatisticsAsync()).Counts.GetValueOrDefault(JobStatus.Queued) == 0)
                {
                    return;
                }
            }
        })));

        Assert.Equal(jobs, leased.Count);
        Assert.Equal(jobs, leased.Select(job => job.Id).Distinct().Count());
        Assert.Equal(jobs, (await harness.Store.StatisticsAsync()).Counts[JobStatus.Running]);
        await AssertJournalIsGapFreeAsync(harness.Store);
    }

    /// <summary>A failing step skips its queued siblings while claims run; every sibling ends either skipped or leased, never both.</summary>
    [Theory, MemberData(nameof(RowLockProviders))]
    public async Task A_failing_flow_and_concurrent_claims_agree_on_each_queued_sibling(string provider)
    {
        const int runs = 30;
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.fail-race", 1).OnFailure(FlowFailurePolicy.Stop)
            .Step<Alpha>("failing", new Alpha("f"), maxAttempts: 1).Step<Beta>("sibling", new Beta("s")).Build();
        var flows = new WorkflowClient(harness.Store);
        for (var i = 0; i < runs; i++)
        {
            await flows.StartAsync(flow, new { });
        }

        var failing = await harness.Store.ClaimBatchAsync(new ClaimRequest("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))
        {
            MaximumJobs = runs
        });
        var leased = new System.Collections.Concurrent.ConcurrentBag<JobRecord>();
        var claims = Enumerable.Range(0, 3).Select(worker => Task.Run(async () =>
        {
            var store = harness.NewStore();
            for (var round = 0; round < runs; round++)
            {
                foreach (var job in await store.ClaimBatchAsync(new ClaimRequest($"claimer-{worker}", [JobContractId.For<Beta>()], ["default"], TimeSpan.FromMinutes(1))
                {
                    MaximumJobs = 2
                }))
                {
                    leased.Add(job);
                }
            }
        }));
        var failures = failing.Select(job => Task.Run(async () =>
            Assert.True(await harness.NewStore().FailAsync(job.Id, job.LeaseToken!.Value, "boom", TimeSpan.Zero))));
        await Task.WhenAll(claims.Concat(failures));

        var siblings = (await harness.Store.ListAsync(new(Limit: 500))).Where(job => job.StepId == "sibling").ToArray();
        Assert.Equal(runs, siblings.Length);
        Assert.All(siblings, sibling => Assert.Equal(leased.Any(job => job.Id == sibling.Id) ? JobStatus.Running : JobStatus.Skipped, sibling.Status));
        foreach (var sibling in siblings.Where(job => job.Status == JobStatus.Skipped))
        {
            Assert.Empty(await harness.Store.AttemptsAsync(sibling.Id));
        }

        await AssertJournalIsGapFreeAsync(harness.Store);
    }

    private static async Task AssertJournalIsGapFreeAsync(RelationalJobStore store)
    {
        var sequences = new List<long>();
        for (var page = await store.ReadEventsAsync(0, 500); page.Count > 0; page = await store.ReadEventsAsync(sequences[^1], 500))
        {
            sequences.AddRange(page.Select(item => item.Sequence));
        }

        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(number => (long)number), sequences);
    }

    [Theory, MemberData(nameof(RowLockProviders))]
    public async Task Job_reads_do_not_fail_while_workers_settle_flows(string provider)
    {
        const int runs = 60;
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.read-contention", 1).Step<Alpha>("first", new Alpha("a")).Step<Beta>("second", new Beta("b")).After("first").Build();
        var correlation = Guid.NewGuid().ToString("N");
        for (var i = 0; i < runs; i++)
        {
            await new WorkflowClient(harness.Store).StartAsync(flow, new { }, correlation);
        }

        using var done = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            var store = harness.NewStore();
            while (!done.IsCancellationRequested)
            {
                if (await store.ClaimAsync(Claim($"worker-{worker}")) is not { } job)
                {
                    if ((await store.StatisticsAsync()).Counts.GetValueOrDefault(JobStatus.Succeeded) == runs * 2)
                    {
                        return;
                    }

                    continue;
                }

                Assert.True(await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
            }
        })).ToArray();
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            var store = harness.NewStore();
            while (!workers.All(worker => worker.IsCompleted))
            {
                foreach (var job in await store.ListAsync(new(correlation, Limit: 500)))
                {
                    _ = await store.AttemptsAsync(job.Id);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers.Concat(readers));
        Assert.Equal(runs * 2, (await harness.Store.StatisticsAsync()).Counts[JobStatus.Succeeded]);
    }

    public static IEnumerable<object[]> TerminalFlowCases() =>
        Providers().SelectMany(provider => new[] { "fail", "release", "expire", "complete" }
            .Select(outcome => new object[] { provider[0], outcome }));

    [Theory, MemberData(nameof(TerminalFlowCases))]
    public async Task Terminal_flow_allows_running_sibling_to_settle_but_never_requeues_it(string provider, string outcome)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var definition = new FlowBuilder("tests.parallel-failure", 1)
            .OnFailure(FlowFailurePolicy.Stop)
            .Step<Alpha>("fails", new Alpha("a"), maxAttempts: 1)
            .Step<Beta>("sibling", new Beta("b"), maxAttempts: 3).Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(definition, new
        {
        });
        var first = (await harness.Store.ClaimAsync(Claim()))!;
        var second = (await harness.Store.ClaimAsync(Claim()))!;
        var failed = first.StepId == "fails" ? first : second;
        var sibling = first.StepId == "sibling" ? first : second;
        Assert.True(await harness.Store.FailAsync(failed.Id, failed.LeaseToken!.Value, "terminal", TimeSpan.Zero));
        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(run))!.Status);
        switch (outcome)
        {
            case "fail":
                Assert.True(await harness.Store.FailAsync(sibling.Id, sibling.LeaseToken!.Value, "retryable", TimeSpan.Zero));
                break;
            case "release":
                Assert.True(await harness.Store.ReleaseAsync(sibling.Id, sibling.LeaseToken!.Value, "shutdown"));
                break;
            case "complete":
                Assert.True(await harness.Store.CompleteAsync(sibling.Id, sibling.LeaseToken!.Value, JobResult.Done()));
                break;
            case "expire":
                Assert.True(await harness.Store.RenewAsync(sibling.Id, sibling.LeaseToken!.Value, TimeSpan.FromMilliseconds(100)));
                await Task.Delay(180);
                break;
        }

        Assert.Null(await harness.NewStore().ClaimAsync(Claim()));
        var saved = (await harness.Store.GetAsync(sibling.Id))!;
        Assert.Equal(outcome == "complete" ? JobStatus.Succeeded : JobStatus.Skipped, saved.Status);
        Assert.Equal(1, saved.Attempt);
        Assert.NotEqual(AttemptStatus.Running, (await harness.Store.AttemptsAsync(sibling.Id)).Single().Status);
        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Accepted_job_survives_store_restart_without_CLR_identity(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("persisted"), new(CorrelationId: "order:123"));
        var restarted = harness.NewStore();
        await restarted.InitializeAsync();
        var saved = await restarted.GetAsync(id);
        Assert.NotNull(saved);
        Assert.Equal(new JobContractId("tests.alpha", 1), saved.Contract);
        Assert.Equal("{\"value\":\"persisted\"}", saved.Payload.GetRawText());
        Assert.Single(await restarted.ListAsync(new("order:123")));
        Assert.Empty(await restarted.ListAsync(new("another-order")));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Duplicate_enqueue_is_atomic_and_conflicting_input_is_rejected(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new JobClient(harness.NewStore()).EnqueueAsync(new Alpha("same"), new(IdempotencyKey: "request-1")))));
        Assert.Single(ids.Distinct());
        Assert.Single(await harness.Store.ListAsync(new()));
        await Assert.ThrowsAsync<StateConflictException>(() => new JobClient(harness.Store).EnqueueAsync(new Alpha("different"), new(IdempotencyKey: "request-1")));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Independent_workers_share_global_and_type_admission_limits(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SeedLimitsAsync([new(null, 3), new("tests.alpha", 1)]);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 5; i++)
        {
            await client.EnqueueAsync(new Alpha(i.ToString()));
            await client.EnqueueAsync(new Beta(i.ToString()));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => harness.NewStore().ClaimAsync(Claim($"worker-{i}")))));
        var admitted = claims.OfType<JobRecord>().ToArray();
        Assert.Equal(3, admitted.Length);
        Assert.Single(admitted, x => x.Contract.Key == "tests.alpha");
        Assert.Equal(3, admitted.Select(x => x.Id).Distinct().Count());
        foreach (var job in admitted)
        {
            Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        }

        Assert.NotNull(await harness.Store.ClaimAsync(Claim()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_throttled_type_does_not_block_other_ready_types(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SetLimitAsync(new("tests.alpha", 0));
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 105; i++)
        {
            await client.EnqueueAsync(new Alpha(i.ToString()));
        }

        var beta = await client.EnqueueAsync(new Beta("eligible"));
        Assert.Equal(beta, (await harness.Store.ClaimAsync(Claim()))!.Id);
        Assert.All((await harness.Store.ListAsync(new(Limit: 200))).Where(x => x.Contract.Key == "tests.alpha"), job => Assert.Equal(0, job.Attempt));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Stale_worker_cannot_complete_or_renew_after_recovery(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("recover"));
        var abandoned = (await harness.Store.ClaimAsync(Claim("old", TimeSpan.FromMilliseconds(100))))!;
        await Task.Delay(180);
        Assert.False(await harness.Store.RenewAsync(id, abandoned.LeaseToken!.Value, TimeSpan.FromSeconds(30)));
        var recovered = (await harness.NewStore().ClaimAsync(Claim("new")))!;
        Assert.Equal(id, recovered.Id);
        Assert.Equal(2, recovered.Attempt);
        Assert.False(await harness.Store.CompleteAsync(id, abandoned.LeaseToken.Value, JobResult.From("stale")));
        Assert.True(await harness.Store.CompleteAsync(id, recovered.LeaseToken!.Value, JobResult.From("current")));
        Assert.Equal("current", (await harness.Store.GetAsync(id))!.Output!.Value.GetString());
        Assert.Equal([AttemptStatus.Abandoned, AttemptStatus.Succeeded], (await harness.Store.AttemptsAsync(id)).Select(x => x.Status));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Lowering_limits_stops_new_admission_without_cancelling_active_work(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        for (var i = 0; i < 3; i++)
        {
            await client.EnqueueAsync(new Alpha(i.ToString()));
        }

        var first = (await harness.Store.ClaimAsync(Claim()))!;
        await harness.Store.SetLimitAsync(new(null, 0));
        await harness.Store.SeedLimitsAsync([new(null, 20)]);
        Assert.Null(await harness.Store.ClaimAsync(Claim()));
        Assert.Equal(JobStatus.Running, (await harness.Store.GetAsync(first.Id))!.Status);
        Assert.True(await harness.Store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done()));
        await harness.Store.SetLimitAsync(new(null, 1));
        Assert.NotNull(await harness.Store.ClaimAsync(Claim()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Immediate_acceptance_uses_storage_time_even_when_the_producer_clock_is_ahead(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var request = JobClient.CreateRecord(JobContractId.For<Alpha>(), new Alpha("clock skew"));
        var ahead = DateTimeOffset.UtcNow.AddHours(1);
        request = request with { CreatedAt = ahead, AvailableAt = ahead };
        var id = await harness.Store.EnqueueAsync(request);
        var claimed = await harness.Store.ClaimAsync(new("worker", [request.Contract], ["default"], TimeSpan.FromMinutes(1)));
        Assert.NotNull(claimed);
        Assert.Equal(id, claimed.Id);
        Assert.True(claimed.CreatedAt < ahead);
        Assert.Equal(claimed.CreatedAt, claimed.AvailableAt);
        Assert.False(claimed.UseStorageTime);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Submillisecond_due_times_never_round_down_and_aligned_times_stay_exact(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        foreach (var instant in new[] { boundary, boundary.AddTicks(1), boundary.AddTicks(9999), DateTimeOffset.UnixEpoch.AddTicks(-1) })
        {
            var id = await client.EnqueueAsync(new Alpha("precision"), new(NotBefore: instant));
            var saved = (await harness.NewStore().GetAsync(id))!;
            Assert.True(saved.AvailableAt >= instant, "Persisted eligibility must never precede the requested instant.");
            Assert.True(saved.AvailableAt - instant < TimeSpan.FromMilliseconds(1));
            if (instant == boundary)
            {
                Assert.Equal(instant, saved.AvailableAt);
            }
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.EnqueueAsync(new Alpha("unrepresentable"), new(NotBefore: DateTimeOffset.MaxValue)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Missing_handler_and_future_jobs_are_not_consumed(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        var unknown = await client.EnqueueAsync(new Beta("no handler"));
        await client.EnqueueAsync(new Alpha("future"), new(NotBefore: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Null(await harness.Store.ClaimAsync(new("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromSeconds(30))));
        Assert.Equal(0, (await harness.Store.GetAsync(unknown))!.Attempt);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Shutdown_release_preserves_work_and_attempt_history(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("interrupt"), new(MaxAttempts: 1));
        var first = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.True(await harness.Store.ReleaseAsync(id, first.LeaseToken!.Value, "Host shutdown"));
        var second = (await harness.NewStore().ClaimAsync(Claim()))!;
        Assert.True(await harness.Store.CompleteAsync(id, second.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal([AttemptStatus.Interrupted, AttemptStatus.Succeeded], (await harness.Store.AttemptsAsync(id)).Select(x => x.Status));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Retry_budget_and_manual_retry_are_explicit(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("fail"), new(MaxAttempts: 1));
        var first = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.True(await harness.Store.FailAsync(id, first.LeaseToken!.Value, "Expected failure", TimeSpan.Zero));
        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(id))!.Status);
        Assert.True(await harness.Store.RetryAsync(id));
        var retry = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.Equal(2, retry.Attempt);
        Assert.True(await harness.Store.CompleteAsync(id, retry.LeaseToken!.Value, JobResult.Done()));
        Assert.False(await harness.Store.RetryAsync(id));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Cancellation_revokes_ownership_and_cannot_become_success(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("cancel"));
        var first = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.True(await harness.Store.CancelAsync(id));
        Assert.False(await harness.Store.CompleteAsync(id, first.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(JobStatus.Cancelled, (await harness.Store.GetAsync(id))!.Status);
        Assert.Equal(AttemptStatus.Cancelled, (await harness.Store.AttemptsAsync(id)).Single().Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Conditional_flow_resumes_from_persisted_parent_output_without_duplicate_successors(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var definition = new FlowBuilder("tests.flow", 1)
            .Step<Alpha>("check").BindInput("value", "value")
            .Step<Beta>("approved").After("check").WhenOutput("check", "approved", true).BindInput("value", "value", "check")
            .Step<Beta>("rejected").After("check").WhenOutput("check", "approved", false).BindInput("value", "value", "check")
            .Build();
        var runId = await new WorkflowClient(harness.Store).StartAsync(definition, new
        {
            value = "order-123"
        }, "flow:123");
        var root = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.Equal("order-123", root.Payload.GetProperty("value").GetString());
        Assert.True(await harness.Store.CompleteAsync(root.Id, root.LeaseToken!.Value, JobResult.From(new
        {
            approved = true,
            value = "from-output"
        })));
        // Simulate a lost completion response. Retrying settlement cannot activate another child.
        Assert.False(await harness.Store.CompleteAsync(root.Id, root.LeaseToken.Value, JobResult.From(new
        {
            approved = false
        })));
        var restarted = harness.NewStore();
        var child = (await restarted.ClaimAsync(Claim()))!;
        Assert.Equal("approved", child.StepId);
        Assert.Equal("from-output", child.Payload.GetProperty("value").GetString());
        Assert.True(await restarted.CompleteAsync(child.Id, child.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(FlowStatus.Succeeded, (await restarted.GetWorkflowAsync(runId))!.Status);
        var steps = await restarted.ListAsync(new("flow:123"));
        Assert.Equal(3, steps.Count);
        Assert.Equal(JobStatus.Skipped, steps.Single(x => x.StepId == "rejected").Status);
        Assert.Equal(0, steps.Single(x => x.StepId == "rejected").Attempt);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Definition_versions_are_immutable_and_existing_runs_keep_their_graph(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.versioned", 1).Step<Alpha>("first", new Alpha("v1")).Build();
        var client = new WorkflowClient(harness.Store);
        await client.StartAsync(flow, new
        {
        });
        var changed = new FlowBuilder("tests.versioned", 1).Step<Beta>("different", new Beta("v2")).Build();
        await Assert.ThrowsAsync<StateConflictException>(() => client.StartAsync(changed, new { }));
        await client.StartAsync(changed with
        {
            Version = 2
        }, new
        {
        });
        var jobs = await harness.Store.ListAsync(new());
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, x => x.Contract.Key == "tests.alpha");
        Assert.Contains(jobs, x => x.Contract.Key == "tests.beta");
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Invalid_output_binding_fails_flow_without_losing_parent_completion(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.bad-output", 1).Step<Alpha>("first", new Alpha("x"))
            .Step<Beta>("next").After("first").BindInput("value", "missing", "first").Build();
        var id = await new WorkflowClient(harness.Store).StartAsync(flow, new
        {
        });
        var root = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.True(await harness.Store.CompleteAsync(root.Id, root.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(JobStatus.Succeeded, (await harness.Store.GetAsync(root.Id))!.Status);
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(id))!.Status);
    }

    [Fact]
    public async Task Storage_failure_is_not_reported_as_accepted_work()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent", "jobs.db");
        var client = new JobClient(new SqliteJobStore($"Data Source={path}"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.EnqueueAsync(new Alpha("must not disappear")));
    }
}
