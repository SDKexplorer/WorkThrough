namespace WorkThrough;

/// <summary>Restricts query ports to a principal's tenants (ADR-019). Store-wide access returns the original port unchanged.</summary>
/// <remarks>Executions of other tenants, and untenanted executions, are reported exactly as absent ones are.</remarks>
public static class TenantScoping
{
    public static IExecutionQueries ScopedTo(this IExecutionQueries queries, TenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(access);

        return access.IsRestricted ? new ScopedExecutionQueries(queries, access) : queries;
    }

    public static IExecutionInspectionQueries ScopedTo(this IExecutionInspectionQueries inspection, IExecutionQueries queries, TenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(access);

        return access.IsRestricted ? new ScopedInspectionQueries(inspection, new(queries, access), access) : inspection;
    }

    public static IRuntimeQueries ScopedTo(this IRuntimeQueries runtime, IExecutionQueries queries, TenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(access);

        return access.IsRestricted ? new ScopedRuntimeQueries(runtime, new(queries, access)) : runtime;
    }

    /// <summary>The tenant filter to apply: the caller's own tenant filters, narrowed to what the principal may read.</summary>
    public static IReadOnlyList<string> AllowedTenants(TenantAccess access, string? tenantId, IReadOnlyList<string>? tenantIds)
    {
        ArgumentNullException.ThrowIfNull(access);
        IEnumerable<string> requested = access.Tenants ?? throw new InvalidOperationException("Store-wide access has no tenant filter.");
        if (tenantId is not null)
        {
            requested = requested.Where(tenant => tenant == tenantId);
        }

        if (tenantIds is not null)
        {
            requested = requested.Intersect(tenantIds, StringComparer.Ordinal);
        }

        // Ordered so that every page of one search fingerprints the same query.
        return requested.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class ScopedExecutionQueries(IExecutionQueries inner, TenantAccess access) : IExecutionQueries
    {
        public Task<ExecutionStoreContext> GetExecutionContextAsync(CancellationToken cancellationToken = default) =>
            inner.GetExecutionContextAsync(cancellationToken);

        public Task<ExecutionPage> SearchExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            return inner.SearchExecutionsAsync(query with { TenantIds = AllowedTenants(access, query.TenantId, query.TenantIds) }, cancellationToken);
        }

        public async Task<ExecutionLookup> FindExecutionAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var lookup = await inner.FindExecutionAsync(id, cancellationToken);

            return access.Allows(lookup.Execution?.TenantId) ? lookup : lookup with { Execution = null };
        }

        public async Task<ExecutionAttemptPage> ReadAttemptsAsync(Guid id, int afterAttempt = 0, int limit = OperationQueryLimits.DefaultPageSize,
            CancellationToken cancellationToken = default)
        {
            var lookup = await FindExecutionAsync(id, cancellationToken);

            return lookup.Execution is null
                ? new(lookup.StoreId, lookup.ObservedAt, false, [], null)
                : await inner.ReadAttemptsAsync(id, afterAttempt, limit, cancellationToken);
        }
    }

    private sealed class ScopedInspectionQueries(IExecutionInspectionQueries inner, ScopedExecutionQueries lookups, TenantAccess access)
        : IExecutionInspectionQueries
    {
        public async Task<ExecutionDiagnostic> ReadDiagnosticAsync(Guid id, int attempt, int maximumCharacters = OperationQueryLimits.DefaultDiagnosticCharacters,
            CancellationToken cancellationToken = default)
        {
            var lookup = await lookups.FindExecutionAsync(id, cancellationToken);

            return lookup.Execution is null
                ? new(lookup.StoreId, lookup.ObservedAt, id, attempt, false, false, null, false)
                : await inner.ReadDiagnosticAsync(id, attempt, maximumCharacters, cancellationToken);
        }

        /// <summary>Every step of a run carries the run's tenant, so its first node decides. Runs without live nodes look absent.</summary>
        public async Task<FlowInspection> ReadFlowAsync(Guid runId, int nodeOffset = 0, int dependencyOffset = 0, long afterEvent = 0,
            int limit = OperationQueryLimits.DefaultPageSize, CancellationToken cancellationToken = default)
        {
            var probe = await inner.ReadFlowAsync(runId, 0, 0, 0, 1, cancellationToken);
            if (probe.Flow is null || probe.Nodes.Count == 0 || !access.Allows(probe.Nodes[0].TenantId))
            {
                return new(probe.StoreId, probe.ObservedAt, null, [], null, [], null, FlowGraphStatuses.NotLive, [], null,
                    probe.RetainedAfterEvent, probe.LastEventSequence, false);
            }

            return await inner.ReadFlowAsync(runId, nodeOffset, dependencyOffset, afterEvent, limit, cancellationToken);
        }
    }

    private sealed class ScopedRuntimeQueries(IRuntimeQueries inner, ScopedExecutionQueries lookups) : IRuntimeQueries
    {
        /// <summary>Keeps the waiting reason; store-wide capacity and worker evidence describe other tenants' load and are withheld.</summary>
        public async Task<ExecutionReadiness> ReadReadinessAsync(Guid id, int workerExamples = ExecutionReadiness.DefaultWorkerExamples,
            CancellationToken cancellationToken = default)
        {
            var lookup = await lookups.FindExecutionAsync(id, cancellationToken);
            if (lookup.Execution is null)
            {
                return new(lookup.StoreId, lookup.ObservedAt, id, false, ReadinessReasons.NotLive, false, null, false, null, null, null, null,
                    null, [], 0, false, false);
            }

            var readiness = await inner.ReadReadinessAsync(id, workerExamples, cancellationToken);

            return readiness with { Capacity = null, WorkerExamples = [], ObservedMatchingWorkers = 0, WorkerExamplesTruncated = false };
        }

        public Task<RuntimeMetrics> ReadRuntimeMetricsAsync(RuntimeMetricsQuery query, CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("Runtime metrics describe every tenant and require store-wide access.");
    }
}
