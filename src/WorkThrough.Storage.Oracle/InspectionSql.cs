namespace WorkThrough.Storage;

public sealed partial class OracleJobStore
{
    protected override string AuditActorValueSql(string field) =>
        $"JSON_VALUE(CASE WHEN LENGTH(a.actor)<=16384 THEN a.actor ELSE TO_CLOB('{{}}') END,'$.{field}' RETURNING VARCHAR2(2000) NULL ON ERROR)";

    protected override string FlowDependencySql => """
        SELECT DISTINCT predecessor_job.id,successor_job.id
        FROM dj_flows f JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version
        CROSS APPLY JSON_TABLE(CASE WHEN LENGTH(d.definition)<=@maximum THEN d.definition ELSE TO_CLOB('{"steps":[]}') END,
          '$.steps[*]' COLUMNS(step_id VARCHAR2(400) PATH '$.id',
            NESTED PATH '$.after[*]' COLUMNS(dependency_id VARCHAR2(400) PATH '$'))) edges
        JOIN dj_jobs successor_job ON successor_job.workflow_run_id=f.id AND successor_job.step_id=edges.step_id
        JOIN dj_jobs predecessor_job ON predecessor_job.workflow_run_id=f.id AND predecessor_job.step_id=edges.dependency_id
        WHERE f.id=@run ORDER BY successor_job.id,predecessor_job.id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
        """;

    protected override string ReadinessWorkersSql => """
        WITH recent AS (
          SELECT id,seen_at,CASE WHEN LENGTH(details)<=262144 THEN details ELSE TO_CLOB('{}') END AS info
          FROM dj_workers WHERE seen_at>=@fresh ORDER BY seen_at DESC,id FETCH FIRST 501 ROWS ONLY)
        SELECT SUBSTR(r.id,1,512),r.seen_at,COALESCE(JSON_VALUE(r.info,'$.capacity' RETURNING NUMBER NULL ON ERROR),0),
          COALESCE(JSON_VALUE(r.info,'$.running' RETURNING NUMBER NULL ON ERROR),0),COALESCE(c.draining,0),
          CASE WHEN EXISTS(SELECT 1 FROM JSON_TABLE(r.info,'$.processors[*]'
            COLUMNS(processor_key VARCHAR2(120) PATH '$.key',processor_version NUMBER PATH '$.version')) p
            WHERE p.processor_key=j.job_key AND p.processor_version=j.contract_version)
            AND EXISTS(SELECT 1 FROM JSON_TABLE(r.info,'$.queues[*]' COLUMNS(queue VARCHAR2(512) PATH '$')) q WHERE q.queue=j.queue) THEN 1 ELSE 0 END,
          CASE WHEN JSON_VALUE(r.info,'$.processors.type()')='array' AND JSON_VALUE(r.info,'$.queues.type()')='array'
            AND JSON_VALUE(r.info,'$.capacity.type()')='number' AND JSON_VALUE(r.info,'$.running.type()')='number' THEN 1 ELSE 0 END,
          CASE WHEN LENGTH(r.id)>512 THEN 1 ELSE 0 END,
          JSON_VALUE(r.info,'$.acquisition.consecutiveFailures' RETURNING NUMBER NULL ON ERROR)
        FROM recent r JOIN dj_jobs j ON j.id=@id LEFT JOIN dj_worker_controls c ON c.worker_id=r.id
        ORDER BY r.seen_at DESC,r.id
        """;
}
