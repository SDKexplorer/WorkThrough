using WorkThrough;
using WorkThrough.Testing;

/// <summary>The test host from the published package runs delayed work after the clock moves.</summary>
internal static class TestingPackageSmoke
{
    public static async Task RunAsync()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddJobs(typeof(PackageHandler).Assembly)
            .AddJob<InvoiceInput, ExplicitInvoiceProcessor>(new JobDefinition<InvoiceInput>("smoke.testing")));
        var id = await host.Jobs.EnqueueAsync(new PackageJob("later"), new(NotBefore: host.UtcNow.AddDays(1)));
        await host.AdvanceAsync(TimeSpan.FromDays(1));
        if ((await host.WaitForJobAsync(id)).Output?.GetString() != "later")
        {
            throw new InvalidOperationException("The test host did not run delayed work.");
        }

        Console.WriteLine("PASS: the WorkThrough.Testing package ran delayed work after advancing its clock.");
    }
}
