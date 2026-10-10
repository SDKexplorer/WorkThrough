using System.Text.Json;
using WorkThrough;

namespace Demo.Processor;

internal static class InvoiceSchedule
{
    public static async Task EnsurePublishedAsync(IJobSchedules schedules, InvoiceSettings settings, CancellationToken ct)
    {
        if ((await schedules.SchedulesAsync(cancellationToken: ct)).Any(s => s.Definition.Key == InvoiceJobs.HourlySchedule))
        {
            return;
        }

        var definition = new ScheduleDefinition(InvoiceJobs.HourlySchedule, 1, settings.Cron, settings.TimeZone,
            InvoiceJobs.Generate.Identity, JsonSerializer.SerializeToElement(new InvoiceInput(settings.InvoiceId), JobJson.Options),
            Overlap: ScheduleOverlap.Coalesce);

        // Value throws with the operator-facing message when a command is rejected.
        var draft = (await schedules.SaveScheduleDraftAsync(definition, 0, Command("Create sample recurring schedule"), ct)).Value;
        var published = (await schedules.PublishScheduleAsync(definition.Key, definition.Version, draft, Command("Publish validated sample schedule"), ct)).Value;
        _ = (await schedules.SetScheduleEnabledAsync(definition.Key, definition.Version, published, true, Command("Enable sample schedule"), ct)).Value;
    }

    private static AdministrativeCommand Command(string reason) =>
        new(Guid.NewGuid(), new("sample-deployment", nameof(InvoiceSchedule)), reason);
}
