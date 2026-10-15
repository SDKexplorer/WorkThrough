using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public sealed record InvoiceV1(string Number);
public sealed record InvoiceV2(string Number, string Currency);
public sealed record InvoiceV3(string Number, string Currency, decimal Total);

public sealed class InvoiceV3Job : IJob<InvoiceV3>
{
    public Task<JobResult> ExecuteAsync(InvoiceV3 input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From($"{input.Number}:{input.Currency}:{input.Total}"));
}

/// <summary>Older stored inputs served by the current job version after conversion.</summary>
public sealed class UpcastTests
{
    private static readonly JobContractId V1 = new("tests.invoice", 1);
    private static readonly JobDefinition<InvoiceV2> V2 = new("tests.invoice", 2);
    private static readonly JobDefinition<InvoiceV3> V3 = new("tests.invoice", 3);

    // v1 inputs no longer have a type in this build, so they are read as JSON; v2 converts to v3.
    private static Task<WorkThroughTestHost> StartAsync() => WorkThroughTestHost.StartAsync(workthrough => workthrough
        .AddJob<InvoiceV3, InvoiceV3Job>(V3)
        .AddUpcast(V2.Identity, V3, (InvoiceV2 old) => new InvoiceV3(old.Number, old.Currency, 0m))
        .AddUpcast(V1, V2, (JsonElement old) => new InvoiceV2(old.GetProperty("number").GetString()!, "EUR")));

    [Fact]
    public async Task Stored_work_and_flow_steps_at_older_versions_run_on_the_current_job()
    {
        await using var host = await StartAsync();
        var v1 = await host.Jobs.EnqueueAsync(V1, new InvoiceV1("INV-1"));
        var v2 = await host.Jobs.EnqueueAsync(V2, new InvoiceV2("INV-2", "USD"));
        var flow = new FlowBuilder("tests.invoicing", 1).Step("issue", V1, new InvoiceV1("INV-3")).Build();
        var run = await host.Flows.StartAsync(flow, new { });

        Assert.Equal("INV-1:EUR:0", (await host.WaitForJobAsync(v1)).Output!.Value.GetString());
        Assert.Equal("INV-2:USD:0", (await host.WaitForJobAsync(v2)).Output!.Value.GetString());
        Assert.Equal("INV-3:EUR:0", (await host.WaitForFlowAsync(run)).AssertSucceeded().Step("issue").Output<string>());

        // The stored address stays v1; only this build's execution changed.
        Assert.Equal(V1, (await host.Queries.GetAsync(v1))!.Contract);
    }

    [Fact]
    public async Task Preflight_counts_an_upcast_address_as_covered()
    {
        await using var host = await StartAsync();
        await host.Store.SetQueuePausedAsync(JobDefaults.Queue, true, 0, new(Guid.NewGuid(), new("tests", "operator"), "Hold work for preflight"));
        await host.Jobs.EnqueueAsync(V1, new InvoiceV1("INV-4"));
        var catalog = host.Services.GetRequiredService<JobCatalog>();
        var worker = new WorkerCapability("candidate", [.. catalog.Registrations.Select(registration => registration.Identity)], [JobDefaults.Queue]);

        var report = await host.Services.GetRequiredService<DeploymentPreflight>().CheckAsync([worker]);

        Assert.True(report.AllRequirementsCovered);
        Assert.Equal(V3.Identity, catalog.Get(V2.Identity).UpcastTo);
    }

    [Fact]
    public void Upcasts_bridge_versions_of_one_key_to_a_registered_job()
    {
        var catalog = new JobCatalog();
        Assert.Throws<InvalidOperationException>(() => catalog.Upcast(V1, V2, (InvoiceV1 old) => new InvoiceV2(old.Number, "EUR")));

        catalog.Register<InvoiceV3, InvoiceV3Job>(V3);
        Assert.Throws<ArgumentException>(() => catalog.Upcast(new JobContractId("tests.other", 1), V3, (InvoiceV1 old) => new InvoiceV3(old.Number, "EUR", 0)));
        Assert.Throws<ArgumentException>(() => catalog.Upcast(V3.Identity, V3, (InvoiceV3 same) => same));
    }
}
