using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WorkThrough;

[McpServerToolType]
internal sealed class RetentionTools(IRetentionQueries? queries = null)
{
    internal const string Status = "workthrough_get_retention";

    [McpServerTool(Name = Status, ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Inspect persisted automatic-retention policy, pending/last batch, hold/incident counts, up to 100 incident examples and journal/subscriber positions. Requires retention.read plus executions.read. Configuration null means no policy; maintenance stays disabled by default. Excludes archive bodies, hold reasons, lease tokens and credentials. No cleanup, export, purge or configuration change occurs.")]
    public Task<RetentionStatus> GetRetention(CancellationToken cancellationToken) =>
        InspectionTools.ReadSafelyAsync(() => (queries ?? throw new NotSupportedException("Retention queries are not registered.")).ReadRetentionStatusAsync(cancellationToken));
}
