using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough.Storage;

public static class SqlServerRegistration
{
    public static WorkThroughBuilder UseSqlServer(this WorkThroughBuilder builder, string connectionString)
    {
        builder.Services.AddSingleton(new SqlServerJobStore(connectionString));
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IJobQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IExecutionQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IExecutionInspectionQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IOperationHistoryQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IRuntimeQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IRetentionQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IRetentionAdministration>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IRetentionMaintenance>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IJobAdministration>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IWorkflowDefinitions>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IJobDiagnostics>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IJobSchedules>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IDeploymentQueries>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IProcessorRegistry>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IExecutionOperations>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IExecutionArchives>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IWorkflowSignals>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IJobOutbox>(sp => sp.GetRequiredService<SqlServerJobStore>());
        builder.Services.AddSingleton<IExecutionEvents>(sp => sp.GetRequiredService<SqlServerJobStore>());

        return builder;
    }
}
