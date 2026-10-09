namespace WorkThrough.Storage;

public sealed partial class SqlServerJobStore
{
    protected override string AuditActorValueSql(string field) =>
        $"JSON_VALUE(CASE WHEN DATALENGTH(a.actor)<=32768 AND ISJSON(a.actor)=1 THEN a.actor ELSE N'{{}}' END,'$.{field}')";

    protected override string FlowDependencySql => """
        SELECT DISTINCT predecessor_job.id,successor_job.id
        FROM dj_flows f JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version
        CROSS APPLY OPENJSON(CASE WHEN DATALENGTH(d.definition)<=@maximum*2 THEN d.definition ELSE N'{"steps":[]}' END,'$.steps')
          WITH(id nvarchar(400) '$.id',dependencies nvarchar(max) '$.after' AS JSON) step
        CROSS APPLY OPENJSON(step.dependencies) dependency
        JOIN dj_jobs successor_job ON successor_job.workflow_run_id=f.id AND successor_job.step_id=step.id COLLATE Latin1_General_100_BIN2
        JOIN dj_jobs predecessor_job ON predecessor_job.workflow_run_id=f.id AND predecessor_job.step_id=dependency.value COLLATE Latin1_General_100_BIN2
        WHERE f.id=@run ORDER BY successor_job.id,predecessor_job.id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
        """;

    protected override string ReadinessWorkersSql => """
        WITH recent AS (
          SELECT TOP(501) id,seen_at,CASE WHEN DATALENGTH(details)<=524288 AND ISJSON(details)=1 THEN details ELSE N'{}' END AS info
          FROM dj_workers WHERE seen_at>=@fresh ORDER BY seen_at DESC,id)
        SELECT SUBSTRING(r.id,1,512),r.seen_at,COALESCE(TRY_CONVERT(int,JSON_VALUE(r.info,'$.capacity')),0),
          COALESCE(TRY_CONVERT(int,JSON_VALUE(r.info,'$.running')),0),COALESCE(c.draining,0),
          CASE WHEN EXISTS(SELECT 1 FROM OPENJSON(r.info,'$.processors') WITH([key] nvarchar(120) '$.key',version int '$.version') p
            WHERE p.[key] COLLATE Latin1_General_100_BIN2=j.job_key AND p.version=j.contract_version)
            AND EXISTS(SELECT 1 FROM OPENJSON(r.info,'$.queues') q WHERE q.value COLLATE Latin1_General_100_BIN2=j.queue) THEN 1 ELSE 0 END,
          CASE WHEN EXISTS(SELECT 1 FROM OPENJSON(r.info) WHERE [key]='processors' AND type=4)
            AND EXISTS(SELECT 1 FROM OPENJSON(r.info) WHERE [key]='queues' AND type=4)
            AND EXISTS(SELECT 1 FROM OPENJSON(r.info) WHERE [key]='capacity' AND type=2)
            AND EXISTS(SELECT 1 FROM OPENJSON(r.info) WHERE [key]='running' AND type=2) THEN 1 ELSE 0 END,
          CASE WHEN DATALENGTH(r.id)>1024 THEN 1 ELSE 0 END,TRY_CONVERT(int,JSON_VALUE(r.info,'$.acquisition.consecutiveFailures'))
        FROM recent r JOIN dj_jobs j ON j.id=@id LEFT JOIN dj_worker_controls c ON c.worker_id=r.id
        ORDER BY r.seen_at DESC,r.id
        """;
}
