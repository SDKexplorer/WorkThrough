namespace WorkThrough;

public sealed record ProcessorAliasMapping(JobContractId Alias, JobContractId Target);
public sealed record ProcessorRegistrySnapshot(long Revision, IReadOnlyList<ProcessorAliasMapping> Aliases);

/// <summary>Audited, versioned alias activation shared by every worker in the store.</summary>
public interface IProcessorRegistry
{
    Task<ProcessorRegistrySnapshot> ProcessorRegistryAsync(CancellationToken cancellationToken = default);
    Task<long> SetProcessorAliasesAsync(IReadOnlyList<ProcessorAliasMapping> aliases, long expectedRevision,
        AdministrativeCommand command, CancellationToken cancellationToken = default);
}
