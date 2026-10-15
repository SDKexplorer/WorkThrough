namespace WorkThrough;

/// <summary>The calling principal's tenant access for this request, set by the guard before any tool runs (ADR-019).</summary>
internal sealed class McpTenantScope
{
    /// <summary>Tools that answer per execution or run. Every other tool describes the whole store and requires store-wide access.</summary>
    internal static readonly string[] TenantCapableTools =
    [
        ExecutionTools.Context, ExecutionTools.Search, ExecutionTools.Get, ExecutionTools.Attempts,
        InspectionTools.Diagnostics, InspectionTools.Flow, RuntimeTools.Capacity
    ];

    public TenantAccess Access { get; set; } = TenantAccess.AllTenants;
}
