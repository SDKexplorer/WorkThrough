using System.Text.Json;

namespace WorkThrough.Testing;

/// <summary>
/// Stand-ins for jobs, so a test can check a flow's logic — bindings, conditions, branches, compensation — without the jobs' real
/// dependencies. Register stubs instead of the real jobs for the steps under test.
/// </summary>
/// <example>
/// <code>
/// await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
///     .Stub(Payments.Charge, (ChargePayment charge) => new Payment(charge.OrderId, NeedsReview: false))
///     .Reject(Shipping.Ship, "address unknown"));
/// var flow = await host.RunFlowAsync(CheckoutFlow.Definition, order);
/// </code>
/// </example>
public static class JobStubs
{
    /// <summary>Succeeds with the output <paramref name="output"/> computes from each input.</summary>
    public static WorkThroughBuilder Stub<TInput, TOutput>(this WorkThroughBuilder builder, JobDefinition<TInput> definition, Func<TInput, TOutput> output) =>
        builder.AddJob(definition, job => Task.FromResult(JobResult.From(output(job.Input))));

    /// <summary>Succeeds without output.</summary>
    public static WorkThroughBuilder Stub<TInput>(this WorkThroughBuilder builder, JobDefinition<TInput> definition) =>
        builder.AddJob(definition, _ => Task.CompletedTask);

    /// <summary>Succeeds with <paramref name="output"/> for work at <paramref name="address"/>, whatever its input type.</summary>
    public static WorkThroughBuilder Stub(this WorkThroughBuilder builder, JobContractId address, object? output = null) =>
        builder.AddJob(new JobDefinition<JsonElement>(address), _ => Task.FromResult(JobResult.From(output)));

    /// <summary>Rejects every input with <paramref name="reason"/>, without retries, so the flow's failure handling runs at once.</summary>
    public static WorkThroughBuilder Reject<TInput>(this WorkThroughBuilder builder, JobDefinition<TInput> definition, string reason) =>
        builder.AddJob(definition, _ => Task.FromResult(JobResult.Reject(reason)));

    /// <summary>Rejects work at <paramref name="address"/> with <paramref name="reason"/>, whatever its input type.</summary>
    public static WorkThroughBuilder Reject(this WorkThroughBuilder builder, JobContractId address, string reason) =>
        builder.AddJob(new JobDefinition<JsonElement>(address), _ => Task.FromResult(JobResult.Reject(reason)));
}
