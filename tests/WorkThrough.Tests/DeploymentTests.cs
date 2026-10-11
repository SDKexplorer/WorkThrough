using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

public sealed class DeploymentTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "deployment"), "Verify proposed deployment");

    [Theory, MemberData(nameof(Providers))]
    public async Task Requires_exact_processor_version_and_queue_on_the_same_worker(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var processor = new JobContractId("moved.processor", 2);
        await new JobClient(harness.Store).EnqueueAsync(processor, "secret", new(Queue: "billing"));
        var preflight = new DeploymentPreflight(harness.Store);
        var old = await preflight.CheckAsync([new("old", [new("moved.processor", 1)], ["billing"])]);
        Assert.Equal(DeploymentCoverage.MissingProcessor, Assert.Single(old.Checks).Coverage);
        var split = await preflight.CheckAsync([new("processor", [processor], ["default"]), new("queue", [], ["billing"])]);
        Assert.Equal(DeploymentCoverage.MissingQueue, Assert.Single(split.Checks).Coverage);
        Assert.False(split.AllRequirementsCovered);
        var moved = await preflight.CheckAsync([new("replacement-assembly", [processor], ["billing"])]);
        Assert.True(moved.AllRequirementsCovered);
        Assert.Equal(["replacement-assembly"], Assert.Single(moved.Checks).Workers);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(moved));
        Assert.Empty(await harness.Store.AttemptsAsync((await harness.Store.ListAsync(new())).Single().Id));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Includes_incident_steps_and_waiting_successors_but_not_completed_history(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.deployment", 1).Step<Alpha>("first", new Alpha("a"), maxAttempts: 1)
            .Step<Beta>("next", new Beta("b")).After("first").Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, new
        {
        });
        var first = (await harness.Store.ClaimAsync(new("w", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.FailAsync(first.Id, first.LeaseToken!.Value, "incident", TimeSpan.Zero);
        var incident = await harness.Store.DeploymentRequirementsAsync();
        Assert.Equal(2, incident.Items.Count);
        Assert.All(incident.Items, x =>
        {
            Assert.Equal(1, x.OutstandingJobs);
            Assert.Equal(0, x.ReadyJobs);
            Assert.Null(x.OldestReadyAt);
        });
        await harness.Store.RecoverStepAsync(first.Id, 1, Command()).SucceedsAsync();
        var retry = (await harness.Store.ClaimAsync(new("w", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))))!;
        var before = (await harness.Store.DeploymentRequirementsAsync()).ObservedAt;
        await Task.Delay(30);
        await harness.Store.CompleteAsync(retry.Id, retry.LeaseToken!.Value, JobResult.Done());
        var successor = Assert.Single((await harness.Store.DeploymentRequirementsAsync()).Items);
        Assert.Equal(JobContractId.For<Beta>(), successor.Processor);
        Assert.Equal(1, successor.ReadyJobs);
        Assert.True(successor.OldestReadyAt > before);
        await harness.Store.SetFlowPausedAsync(run, true, Command()).SucceedsAsync();
        Assert.Equal(0, Assert.Single((await harness.Store.DeploymentRequirementsAsync()).Items).ReadyJobs);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Includes_enabled_schedule_targets_and_distinguishes_future_work_from_ready_work(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        await new JobClient(store).EnqueueAsync(new Alpha("future"), new(NotBefore: DateTimeOffset.UtcNow.AddDays(1)));
        var schedule = new ScheduleDefinition("tests.nightly", 1, "0 0 * * *", "UTC", JobContractId.For<Beta>(), JsonSerializer.SerializeToElement("secret"));
        await store.SaveScheduleDraftAsync(schedule, 0, Command()).SucceedsAsync();
        Assert.Single((await store.DeploymentRequirementsAsync()).Items);
        await store.PublishScheduleAsync(schedule.Key, 1, 1, Command()).SucceedsAsync();
        Assert.Single((await store.DeploymentRequirementsAsync()).Items);
        await store.SetScheduleEnabledAsync(schedule.Key, 1, 2, true, Command()).SucceedsAsync();
        var report = await store.DeploymentRequirementsAsync();
        Assert.Equal(2, report.Items.Count);
        Assert.All(report.Items, x =>
        {
            Assert.Equal(0, x.ReadyJobs);
            Assert.Null(x.OldestReadyAt);
        });
        var target = Assert.Single(report.Items, x => x.Processor == schedule.Processor);
        Assert.Equal(1, target.EnabledSchedules);
        Assert.Equal(0, target.OutstandingJobs);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_truncated_requirement_inventory_cannot_report_complete_coverage(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using DbConnection connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<1001)
            INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,status,attempt,max_attempts,created_at,available_at)
            SELECT CAST(n AS TEXT),'tests.group',n,'{}','default','Queued',0,3,0,0 FROM numbers
            """;
        if (provider == "sqlserver")
        {
            command.CommandText = command.CommandText.Replace("WITH RECURSIVE", "WITH", StringComparison.Ordinal)
                .Replace("AS TEXT", "AS nvarchar(36)", StringComparison.Ordinal) + " OPTION(MAXRECURSION 0)";
        }

        if (provider == "oracle")
        {
            command.CommandText = """
                INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,status,attempt,max_attempts,created_at,available_at)
                SELECT TO_CHAR(LEVEL),'tests.group',LEVEL,'{}','default','Queued',0,3,0,0 FROM dual CONNECT BY LEVEL<=1001
                """;
        }

        await command.ExecuteNonQueryAsync();
        var requirements = await harness.Store.DeploymentRequirementsAsync();
        Assert.Equal(1000, requirements.Items.Count);
        Assert.True(requirements.IsTruncated);
        var report = DeploymentPreflight.Compare(requirements, [new("all", requirements.Items.Select(x => x.Processor).ToArray(), ["default"])]);
        Assert.All(report.Checks, c => Assert.Equal(DeploymentCoverage.Covered, c.Coverage));
        Assert.False(report.AllRequirementsCovered);
    }
}
