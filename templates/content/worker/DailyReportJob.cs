using WorkThrough;

namespace WorkThroughWorker;

// The address "reports.daily" is what the database stores. Rename or move these types freely; keep the address.
// Change the address version when the input's meaning changes.
public static class Reports
{
    public static JobDefinition<DailyReport> Daily { get; } = new("reports.daily", displayName: "Daily report");
}

public sealed record DailyReport(int Days);

public sealed class DailyReportJob(ILogger<DailyReportJob> logger) : IJob<DailyReport>
{
    public Task<JobResult> ExecuteAsync(DailyReport input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // An attempt can repeat after a crash. Pass context.IdempotencyKey to services that must not apply an effect twice.
        logger.LogInformation("Building the report for the last {Days} day(s), attempt {Attempt}", input.Days, context.Attempt);

        return Task.FromResult(JobResult.Done());
    }
}
