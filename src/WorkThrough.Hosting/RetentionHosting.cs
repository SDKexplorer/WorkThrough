using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

public static class RetentionHosting
{
    public static WorkThroughBuilder AddRetentionMaintenance(this WorkThroughBuilder builder, Action<RetentionWorkerOptions>? configure = null)
    {
        var options = new RetentionWorkerOptions();
        configure?.Invoke(options);
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<RetentionWorker>();

        return builder;
    }

    public static WorkThroughBuilder AddArchiveDirectory(this WorkThroughBuilder builder, string destinationId, string directory)
    {
        builder.Services.AddSingleton<IArchiveDestination>(new FileArchiveDestination(destinationId, directory));

        return builder;
    }
}
