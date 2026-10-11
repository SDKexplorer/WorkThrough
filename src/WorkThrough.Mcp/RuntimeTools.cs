using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WorkThrough;

[McpServerToolType]
internal sealed class RuntimeTools(IExecutionQueries executions, McpTenantScope tenantScope, IRuntimeQueries? queries = null)
{
    internal const string Capacity = "workthrough_get_capacity";
    internal const string Metrics = "workthrough_get_metrics";
    internal static readonly string[] Names = [Capacity, Metrics];

    [McpServerTool(Name = Capacity, ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Explain one execution's waiting/capacity state in a read snapshot. id is the execution ID. Returns shared admitted-lease counts/limits, flow/queue state, deadlines and recent matching-worker examples, never payload/progress text or lease tokens. At most 501 recent workers are examined; incomplete coverage never means no capable worker exists. Heartbeats are observations, not proof of future acquisition or free per-processor local slots. workerExamples is 1–100, default 10. No mutation occurs.")]
    public Task<ExecutionReadiness> GetCapacity(Guid id, CancellationToken cancellationToken, int workerExamples = 10) =>
        InspectionTools.ReadSafelyAsync(() => Queries.ReadReadinessAsync(id, workerExamples, cancellationToken));

    [McpServerTool(Name = Metrics, ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read live attempt metrics in a half-open completion-time window, default one hour/max seven days. Returns finished/succeeded/failed attempt counts, mean/max duration and successful attempts per second. These are attempts, not distinct jobs or external effects. Archived attempts are excluded; empty windows have null durations. No payload, SQL or processor arguments accepted.")]
    public Task<RuntimeMetrics> GetMetrics(RuntimeMetricsQuery query, CancellationToken cancellationToken) =>
        InspectionTools.ReadSafelyAsync(() => Queries.ReadRuntimeMetricsAsync(query, cancellationToken));

    private IRuntimeQueries Queries =>
        (queries ?? throw new NotSupportedException("Runtime queries are not registered.")).ScopedTo(executions, tenantScope.Access);
}
