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

        return new(services, catalog, initialization);
    }
}
