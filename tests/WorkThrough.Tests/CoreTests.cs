using Xunit;

namespace WorkThrough.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Core_has_no_database_web_or_hosting_dependencies()
    {
        var dependencies = typeof(JobClient).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(dependencies, x => x.Name!.StartsWith("Microsoft.Extensions", StringComparison.Ordinal)
            || x.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || x.Name == "Npgsql" || x.Name == "Microsoft.Data.SqlClient");
    }

    [Fact]
    public void Persisted_state_names_are_a_storage_contract()
    {
        Assert.Equal(["Running", "Paused", "Incident", "Succeeded", "Failed"], Enum.GetNames<FlowStatus>());
        Assert.Equal(["Running", "Succeeded", "Failed", "Rejected", "Deferred", "Interrupted", "Abandoned", "Cancelled"], Enum.GetNames<AttemptStatus>());
        Assert.Equal(["Job", "Flow"], Enum.GetNames<ArchiveKind>());

        // Archives and stored batch results use the shared wire settings, which have no enum converter of their own.
        Assert.Equal("\"Incident\"", System.Text.Json.JsonSerializer.Serialize(FlowStatus.Incident, JobJson.Options));
        Assert.Equal("\"Abandoned\"", System.Text.Json.JsonSerializer.Serialize(AttemptStatus.Abandoned, JobJson.Options));
        Assert.Equal("\"Flow\"", System.Text.Json.JsonSerializer.Serialize(ArchiveKind.Flow, JobJson.Options));
    }

    [Fact]
    public void Explicit_stable_identity_is_required()
    {
        Assert.Throws<InvalidOperationException>(() => JobContractId.For<Unmarked>());
        Assert.Throws<ArgumentException>(() => new JobContractId("Namespace.Type, Assembly", 1));
    }

    [Fact]
    public void Duplicate_implementations_fail_before_work_is_claimed()
    {
        var catalog = new JobCatalog();
        catalog.Register<Alpha, FirstHandler>();
        catalog.Register<Alpha, FirstHandler>(); // Shared modules may appear through multiple references.
        Assert.Single(catalog.Registrations);
        Assert.Throws<InvalidOperationException>(() => catalog.Register<Alpha, OtherHandler>());
    }

    [Fact]
    public void Workflow_rejects_cycles_and_reads_from_unrelated_steps()
    {
        Assert.Throws<ArgumentException>(() => new FlowBuilder("tests.cycle", 1)
            .Step<Alpha>("a").After("b").Step<Beta>("b").After("a").Build());
        Assert.Throws<ArgumentException>(() => new FlowBuilder("tests.unrelated", 1)
            .Step<Alpha>("a").Step<Beta>("b").BindInput("value", "value", "a").Build());
    }

    private sealed record Unmarked(string Value);

    [Fact]
    public async Task Processors_with_the_same_input_type_dispatch_by_processor_identity()
    {
        var catalog = new JobCatalog();
        catalog.Register<string, FirstProcessor>();
        catalog.Register<string, SecondProcessor>();
        catalog.Register<string, FirstProcessor>();
        Assert.Equal(2, catalog.Registrations.Count);
        var context = new JobExecutionContext(Guid.NewGuid(), 1, null, null, null, Guid.NewGuid());
        var input = System.Text.Json.JsonSerializer.SerializeToElement("{\"value\":42}");
        var first = await catalog.Get(JobIdentity.For<FirstProcessor>()).InvokeAsync(new FirstProcessor(), input, context, default);
        var second = await catalog.Get(JobIdentity.For<SecondProcessor>()).InvokeAsync(new SecondProcessor(), input, context, default);
        Assert.Equal("first:{\"value\":42}", first.Output.GetString());
        Assert.Equal("second:{\"value\":42}", second.Output.GetString());
        Assert.Throws<InvalidOperationException>(() => catalog.Register<string, ConflictingProcessor>());
    }

    [Fact]
    public void Raw_string_steps_are_valid_but_property_bindings_require_an_object()
    {
        var flow = new FlowBuilder("tests.raw", 1).Step("first", JobIdentity.For<FirstProcessor>(), "raw input").Build();
        Assert.Equal("raw input", flow.Steps[0].Input.GetString());
        Assert.Throws<ArgumentException>(() => new FlowBuilder("tests.raw", 1)
            .Step("first", JobIdentity.For<FirstProcessor>(), "raw input").BindInput("a", "a").Build());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    public async Task Required_nonnullable_input_is_validated_on_every_supported_runtime(string json)
    {
        var catalog = new JobCatalog();
        catalog.Register<Alpha, FirstHandler>();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var context = new JobExecutionContext(Guid.NewGuid(), 1, null, null, null, Guid.NewGuid());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => catalog.Get(JobContractId.For<Alpha>())
            .InvokeAsync(new FirstHandler(), document.RootElement, context, default));
    }

    [Job("tests.first")]
    private sealed class FirstProcessor : IJob<string>
    {
        public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(JobResult.From("first:" + input));
    }
    [Job("tests.second")]
    private sealed class SecondProcessor : IJob<string>
    {
        public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(JobResult.From("second:" + input));
    }
    [Job("tests.first")]
    private sealed class ConflictingProcessor : IJob<string>
    {
        public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(JobResult.Done());
    }
    private sealed class FirstHandler : IJob<Alpha>
    {
        public Task<JobResult> ExecuteAsync(Alpha job, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Done());
    }
    private sealed class OtherHandler : IJob<Alpha>
    {
        public Task<JobResult> ExecuteAsync(Alpha job, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Done());
    }

    [Fact]
    public void Typed_bindings_save_the_same_definition_as_property_names()
    {
        var typed = new FlowBuilder("tests.typed", 1)
            .Step("first", JobContractId.For<Alpha>())
            .Step("second", JobContractId.For<Beta>()).After("first")
                .BindInput((Beta beta) => beta.Value, (Alpha alpha) => alpha.Value, "first")
                .WhenOutput("first", (Alpha alpha) => alpha.Value, "ready")
            .Build();
        var named = new FlowBuilder("tests.typed", 1)
            .Step("first", JobContractId.For<Alpha>())
            .Step("second", JobContractId.For<Beta>()).After("first")
                .BindInput("value", "value", "first")
                .WhenOutput("first", "value", "ready")
            .Build();

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(named, JobJson.Options), System.Text.Json.JsonSerializer.Serialize(typed, JobJson.Options));
    }

    private sealed record PickList(IReadOnlyList<string> Parcels);

    [Fact]
    public void A_fan_out_saves_json_names_depends_on_its_source_and_requires_its_protocol()
    {
        var typed = new FlowBuilder("tests.fan-out", 1)
            .Step("pick", JobContractId.For<Alpha>())
            .ForEach("ship", JobContractId.For<Beta>(), "pick", (PickList list) => list.Parcels, (Beta beta) => beta.Value)
            .Build();
        var named = new FlowBuilder("tests.fan-out", 1)
            .Step("pick", JobContractId.For<Alpha>())
            .ForEach("ship", JobContractId.For<Beta>(), "pick", "parcels", "value")
            .Build();

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(named, JobJson.Options), System.Text.Json.JsonSerializer.Serialize(typed, JobJson.Options));
        Assert.Equal(["pick"], typed.Steps[1].After);
        Assert.Equal(EngineProtocol.FanOut, typed.RequiredProtocol);
        Assert.Equal(EngineProtocol.Baseline, new FlowBuilder("tests.plain", 1).Step("only", JobContractId.For<Alpha>()).Build().RequiredProtocol);
        Assert.Throws<ArgumentException>(() => new FlowBuilder("tests.brackets", 1).Step("ship[0]", JobContractId.For<Alpha>()).Build());
        Assert.Throws<ArgumentException>(() => new FlowBuilder("tests.collision", 1)
            .Step("pick", JobContractId.For<Alpha>())
            .ForEach("ship", JobContractId.For<Beta>(), "pick", "parcels", "value").BindInput("value", "value", "pick")
            .Build());
    }
}
