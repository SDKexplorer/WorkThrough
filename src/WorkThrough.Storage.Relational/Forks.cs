using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Checkpoint forks: a new run that keeps the work before a step and runs the rest again.</summary>
public abstract partial class RelationalJobStore
{
    public Task<Result<Guid>> ForkRunAsync(Guid runId, string fromStep, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromStep);

        return AdminAsync(command, AuditActions.ForkRun, runId.ToString(), Args(("fromStep", fromStep)), async (connection, transaction, ct) =>
        {
            await LockFlowAsync(connection, transaction, runId, skipLocked: false, ct);
            FlowDefinition definition;
            JsonElement input;
            string? correlation;
            int? maxParallel;
            await using (var command = Command(connection, transaction, """
                SELECT f.input,d.definition,f.correlation_id,f.max_parallel,f.status FROM dj_flows f JOIN dj_definitions d
                ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id
                """, ("id", runId.ToString())))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new MissingStateException("The flow does not exist in the active store.");
                }

                if (Enum.Parse<FlowStatus>(reader.GetString(4)) is not (FlowStatus.Succeeded or FlowStatus.Failed or FlowStatus.Incident))
                {
                    throw new StateConflictException("Fork a flow that has finished, failed or stopped in an incident; a running flow is still changing.");
                }

                input = Revealed(reader.GetString(0));
                definition = JobJson.Deserialize<FlowDefinition>(reader.GetString(1))!;
                correlation = reader.IsDBNull(2) ? null : reader.GetString(2);
                maxParallel = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3));
            }

            if (definition.Steps.All(step => step.Id != fromStep))
            {
                throw new StateConflictException($"Flow {definition.Key} v{definition.Version} has no step {fromStep}.");
            }

            var rerun = definition.Descendants(fromStep).Append(fromStep).ToHashSet(StringComparer.Ordinal);
            var jobs = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE workflow_run_id=@id", ct, ("id", runId.ToString()));
            var kept = jobs.Where(job => job.StepId is { } step && !rerun.Contains(step) && definition.Steps.Any(s => s.Id == step)
                && job.Status is JobStatus.Succeeded or JobStatus.Skipped).ToDictionary(job => job.StepId!);
            var keys = jobs.FirstOrDefault();

            return await InsertRunAsync(connection, transaction, definition, input,
                new(correlation, keys?.PartitionKey, keys?.TenantId, maxParallel, keys?.TraceParent, ForkedFromRunId: runId), ct, kept);
        }, cancellationToken);
    }
}
