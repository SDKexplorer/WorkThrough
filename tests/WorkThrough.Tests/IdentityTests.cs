using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

public sealed class IdentityTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "release"), "Activate compatible identity rename");
    private static readonly JobDefinition<Input> Definition = new("orders.current", displayName: "Process order");
    private static readonly JobContractId Old = new("orders.legacy", 1);
    public sealed record Input(string OrderId);
    private sealed class Processor : IProcessor<Input>
    {
        public Task<JobResult> ProcessAsync(Input input, JobExecutionContext context, CancellationToken ct) => Task.FromResult(JobResult.From(input.OrderId));
    }

    [Fact]
    public async Task Typed_definition_binds_unattributed_code_and_checks_saved_payloads()
    {
        var catalog = new JobCatalog();
        catalog.RegisterProcessor<Input, Processor>(Definition, Old);
        var registration = catalog.Get(Old);
        Assert.Equal(Definition.Identity, registration.CanonicalIdentity);
        Assert.Equal("Process order", registration.DisplayName);
        using var valid = JsonDocument.Parse("{\"orderId\":\"old-payload\"}");
        var input = Definition.ReadInput(valid.RootElement);
        var result = await registration.InvokeAsync(new Processor(), valid.RootElement, new(Guid.NewGuid(), 1, null, null, null, Guid.NewGuid()), default);
        Assert.Equal(input.OrderId, result.Output.GetString());
        using var invalid = JsonDocument.Parse("{}");
        Assert.Throws<JsonException>(() => Definition.ReadInput(invalid.RootElement));
        Assert.Throws<InvalidOperationException>(() => catalog.ValidateRegistry(new(0, [])));
        catalog.ValidateRegistry(new(1, catalog.Aliases));
        catalog.Seal();
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterProcessor<Input, Processor>(Definition));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Alias_rename_preserves_data_shares_limits_and_fences_old_workers(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var client = new JobClient(store);
        var oldJob = await client.EnqueueAsync(Old, new Input("one"), new(IdempotencyKey: "order-one"));
        var newJob = await client.EnqueueAsync(Definition, new Input("two"));
        await store.SeedLimitsAsync([new(Old.Key, 1)]);
        var command = Command();
        var aliases = new[] { new ProcessorAliasMapping(Old, Definition.Identity) };
        var revision = await store.SetProcessorAliasesAsync(aliases, 0, command).SucceedsAsync();
        Assert.Equal(revision, await store.SetProcessorAliasesAsync(aliases, 0, command).SucceedsAsync());
        await Assert.ThrowsAsync<ProcessorRegistryChangedException>(() => store.ClaimAsync(new("stale", [Old], ["default"], TimeSpan.FromSeconds(30))));
        var claim = new ClaimRequest("current", [Old, Definition.Identity], ["default"], TimeSpan.FromSeconds(30), revision);
        var first = (await store.ClaimAsync(claim))!;
        Assert.Null(await harness.NewStore().ClaimAsync(claim));
        await store.SetProcessorAliasesAsync(aliases, revision, Command()).FailsAsync(ErrorType.Conflict);
        Assert.True(await store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done()));
        Assert.NotNull(await store.ClaimAsync(claim));
        Assert.Equal(Old, (await store.GetAsync(oldJob))!.Contract);
        Assert.Equal(Definition.Identity, (await store.GetAsync(newJob))!.Contract);
        Assert.Equal(oldJob, await client.EnqueueAsync(Old, new Input("one"), new(IdempotencyKey: "order-one")));
        await Assert.ThrowsAsync<StateConflictException>(() => client.EnqueueAsync(Definition, new Input("one"), new(IdempotencyKey: "order-one")));
        Assert.Single(await store.AuditAsync());
    }
}
