using WorkThrough;

namespace Demo.Processor;

public static class InvoiceJobs
{
    public const string HourlySchedule = "invoices.hourly";
    public static JobDefinition<InvoiceInput> Generate { get; } = new("invoices.generate", displayName: "Generate invoice");
    public static string CorrelationId(string invoiceId) => $"invoice:{invoiceId}";
}

public sealed record InvoiceInput(string InvoiceId);
public sealed record InvoiceOutput(string InvoiceId, bool Generated);

public sealed class InvoiceSettings
{
    public const string SectionName = "Jobs";
    public string ConnectionString { get; init; } = "Data Source=processor-sample.db";
    public string InvoiceId { get; init; } = "sample-123";
    public string Cron { get; init; } = "0 * * * *";
    public string TimeZone { get; init; } = "UTC";
    public int FleetConcurrency { get; init; } = 4;
    public int InvoiceConcurrency { get; init; } = 2;
    public int LocalConcurrency { get; init; } = 2;
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Delay { get; init; } = TimeSpan.FromMinutes(5);
}
