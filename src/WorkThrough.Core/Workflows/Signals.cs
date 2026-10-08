using System.Text.Json;

namespace WorkThrough;

/// <summary>A stable signal name. Its JSON meaning is part of the pinned flow revision.</summary>
public sealed record SignalDefinition<TInput>
{
    public SignalDefinition(string name)
    {
        _ = new JobContractId(name, 1);
        Name = name;
    }
    public string Name { get; }
}
public sealed record SignalWait(string Name, TimeSpan Timeout)
{
    public static JobContractId Processor { get; } = new("workthrough.signal", 1);
}
public interface IWorkflowSignals
{
    Task<bool> SignalAsync(Guid runId, string name, JsonElement input, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<int> DispatchSignalsAsync(int batchSize = 100, CancellationToken cancellationToken = default);
}
