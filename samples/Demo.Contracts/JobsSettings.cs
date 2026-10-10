namespace Demo.Contracts;

/// <summary>Bind the Jobs section; environment variables such as Jobs__ConnectionString override the sample defaults.</summary>
public sealed class JobsSettings
{
    public const string SectionName = "Jobs";
    public string ConnectionString { get; init; } = "Data Source=durablejobs-demo.db";
    public string? PostgreSql { get; init; }
    public string? Oracle { get; init; }
    public string? SqlServer { get; init; }
    public bool RunWorker { get; init; } = true;
    public int LocalConcurrency { get; init; } = 4;
    public int FleetConcurrency { get; init; } = 12;
    public int ShippingConcurrency { get; init; } = 2;
}
