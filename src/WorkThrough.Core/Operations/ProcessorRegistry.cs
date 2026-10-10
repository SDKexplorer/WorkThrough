namespace WorkThrough;

public sealed record ProcessorAliasMapping(JobContractId Alias, JobContractId Target);

public sealed record ProcessorRegistrySnapshot(long Revision, IReadOnlyList<ProcessorAliasMapping> Aliases);

/// <summary>The worker validated an older registry revision. It must restart with the reviewed mappings before claiming work.</summary>
public sealed class ProcessorRegistryChangedException()
    : StateConflictException("Processor registry changed. Restart this worker with the current mappings before claiming work.");

/// <summary>Audited, versioned alias activation shared by every worker in the store.</summary>
public interface IProcessorRegistry
{
    Task<ProcessorRegistrySnapshot> ProcessorRegistryAsync(CancellationToken cancellationToken = default);
    /// <summary>The value is the new registry revision.</summary>
    Task<Result<long>> SetProcessorAliasesAsync(IReadOnlyList<ProcessorAliasMapping> aliases, long expectedRevision,
        AdministrativeCommand command, CancellationToken cancellationToken = default);
}
