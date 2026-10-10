using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough.Storage;

public static class OracleRegistration
{
    public static WorkThroughBuilder UseOracle(this WorkThroughBuilder builder, string connectionString, OracleStorageOptions? options = null)
    {
        builder.Services.AddSingleton(new OracleJobStore(connectionString, options));
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IJobQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IExecutionQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IExecutionInspectionQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IOperationHistoryQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IRuntimeQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IRetentionQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IRetentionAdministration>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IRetentionMaintenance>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IJobAdministration>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IWorkflowDefinitions>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IJobDiagnostics>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IJobSchedules>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IDeploymentQueries>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IProcessorRegistry>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IExecutionOperations>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IExecutionArchives>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IWorkflowSignals>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IJobOutbox>(sp => sp.GetRequiredService<OracleJobStore>());
        builder.Services.AddSingleton<IExecutionEvents>(sp => sp.GetRequiredService<OracleJobStore>());

        return builder;
    }
}
