using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WorkThrough;

public static class WorkThroughRegistration
{
    public static WorkThroughBuilder AddWorkThrough(this IServiceCollection services)
    {
        if (services.Any(x => x.ServiceType == typeof(JobCatalog)))
        {
            throw new InvalidOperationException("Call AddWorkThrough once per service collection.");
        }

        var catalog = new JobCatalog();
        var initialization = new StorageInitialization();
        services.AddSingleton(catalog);
        services.AddSingleton(initialization);
        services.AddSingleton<JobClient>();
        services.AddSingleton<WorkflowClient>();
        services.AddSingleton<DeploymentPreflight>();
        services.TryAddScoped<IJobRetryPolicy, DefaultRetryPolicy>();
        services.AddHostedService<JobStorageInitializer>();

        var builder = new WorkThroughBuilder(services, catalog, initialization);

        // Schedules can start flows; the job that does it is served by every host, like any other job.
        return builder.AddJob(ScheduledFlows.Start, call => call.Service<WorkflowClient>().StartAsync(call.Input.Definition, call.Input.Input,
            new FlowStart(IdempotencyKey: ScheduledFlows.RunKey(call.Context.JobId)), call.CancellationToken));
    }
}
