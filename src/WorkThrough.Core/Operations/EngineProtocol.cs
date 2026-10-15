namespace WorkThrough;

/// <summary>
/// Levels of shared execution semantics that every host advancing work in one store must understand (ADR-021). A store's active level
/// rises only when every live worker supports it and no older worker holds a lease. Hosts refuse to claim from a store above their level.
/// </summary>
public static class EngineProtocol
{
    /// <summary>Semantics of releases before engine protocols existed.</summary>
    public const int Baseline = 1;

    /// <summary>Flow steps that expand into one item step per element of an earlier output (<see cref="FlowFanOut"/>).</summary>
    public const int FanOut = 2;

    /// <summary>
    /// Claims under shared limits lock per-limit rows instead of the store's admission lock (ADR-022). Every claimant must take those
    /// locks, so older builds cannot claim once it is active.
    /// </summary>
    public const int ConcurrentLimits = 3;

    /// <summary>Flow steps that run another published flow and wait for it (<see cref="FlowChild"/>).</summary>
    public const int ChildFlows = 4;

    /// <summary>Compensating steps that undo completed work when a flow fails (<see cref="FlowCompensation"/>).</summary>
    public const int Compensation = 5;

    /// <summary>Child steps that repeat until their output meets a condition (<see cref="FlowLoop"/>).</summary>
    public const int Loops = 6;

    /// <summary>
    /// Stores may write job inputs, outputs, flow inputs and signals as encrypted envelopes (<see cref="IPayloadProtector"/>). Older
    /// builds would hand an envelope to a job as its input, so no store encrypts before this level is active.
    /// </summary>
    public const int PayloadProtection = 7;

    /// <summary>Steps that run when a predecessor fails, which handles that failure (<see cref="FlowStep.OnFailureOf"/>).</summary>
    public const int FailureBranches = 8;

    /// <summary>
    /// Schedules with seconds, fixed intervals, start and end bounds or excluded dates. Older schedulers would ignore those fields and
    /// fire at the wrong times.
    /// </summary>
    public const int RichSchedules = 9;

    /// <summary>Schedules that skip the times of named, runtime-editable calendars. Older schedulers would ignore the names.</summary>
    public const int NamedCalendars = 10;

    /// <summary>The highest level this build understands.</summary>
    public const int Supported = NamedCalendars;
}

/// <summary>A store's active protocol and, while a higher level waits, the workers that block it.</summary>
/// <param name="Active">The level every host must understand now.</param>
/// <param name="Supported">The highest level this host understands.</param>
/// <param name="Blockers">Workers that are live below the requested level or hold leases without advertising it.</param>
public sealed record EngineProtocolState(int Active, int Supported, IReadOnlyList<string> Blockers);

/// <summary>Reads and raises a store's engine protocol.</summary>
public interface IEngineProtocol
{
    Task<EngineProtocolState> EngineProtocolAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises the active level to <paramref name="level"/> when no live worker below it remains and every running attempt belongs to a
    /// live worker that supports it. Otherwise leaves the level unchanged and reports the blockers. Never lowers the level.
    /// </summary>
    Task<EngineProtocolState> ActivateEngineProtocolAsync(int level, CancellationToken cancellationToken = default);
}

/// <summary>The store requires an engine protocol this build does not understand. Upgrade this host.</summary>
public sealed class EngineProtocolException(int active)
    : InvalidOperationException($"The store's engine protocol is {active}, above the {EngineProtocol.Supported} this WorkThrough build understands. Upgrade this host.")
{
    public int Active { get; } = active;
}
