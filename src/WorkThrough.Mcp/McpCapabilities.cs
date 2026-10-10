using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

/// <summary>Optional query ports and the tools and capabilities each one enables.</summary>
internal static class McpCapabilities
{
    private sealed record Capability(Type Port, string[] Tools, string[] Names);

    private static readonly Capability[] Optional =
    [
        new(typeof(IExecutionInspectionQueries), InspectionTools.Names, ["diagnostic-text", "flow-graph"]),
        new(typeof(IOperationHistoryQueries), HistoryTools.Names, ["audit", "archives", "retention-preview"]),
        new(typeof(IRuntimeQueries), RuntimeTools.Names, ["capacity", "metrics"]),
        new(typeof(IRetentionQueries), [RetentionTools.Status], ["retention-status"])
    ];

    /// <summary>This transport never exposes commands.</summary>
    private const string Mutations = "mutations";

    internal static bool IsAvailable(IServiceProvider services, string tool) =>
        Optional.All(capability => !capability.Tools.Contains(tool) || services.GetService(capability.Port) is not null);

    internal static IReadOnlyList<string> Unavailable(IServiceProvider services) =>
    [
        Mutations, .. Optional.Where(capability => services.GetService(capability.Port) is null).SelectMany(capability => capability.Names)
    ];
}
