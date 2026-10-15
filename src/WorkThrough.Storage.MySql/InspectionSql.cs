namespace WorkThrough.Storage;

public sealed partial class MySqlJobStore
{
    // Only JSON functions that MySQL and MariaDB share. JSON_CONTAINS compares strings exactly, like the binary collation of the tables.
    protected override string AuditActorValueSql(string field) =>
        $"JSON_UNQUOTE(JSON_EXTRACT(CASE WHEN CHAR_LENGTH(a.actor)<=16384 THEN a.actor ELSE '{{}}' END,'$.{field}'))";

    protected override string FlowDependencySql => """
        SELECT DISTINCT predecessor_job.id,successor_job.id
        FROM dj_flows f JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version
        CROSS JOIN JSON_TABLE(CASE WHEN CHAR_LENGTH(d.definition)<=@maximum THEN d.definition ELSE '{"steps":[]}' END,
          '$.steps[*]' COLUMNS(step_id VARCHAR(400) PATH '$.id',
            NESTED PATH '$.after[*]' COLUMNS(dependency_id VARCHAR(400) PATH '$'))) edges
        JOIN dj_jobs successor_job ON successor_job.workflow_run_id=f.id AND successor_job.step_id=BINARY edges.step_id
        JOIN dj_jobs predecessor_job ON predecessor_job.workflow_run_id=f.id AND predecessor_job.step_id=BINARY edges.dependency_id
        WHERE f.id=@run ORDER BY successor_job.id,predecessor_job.id LIMIT @limit OFFSET @offset
        """;

    protected override string ReadinessWorkersSql => $"""
        WITH recent AS (
          SELECT id,seen_at,CASE WHEN CHAR_LENGTH(details)<=262144 THEN details ELSE '{"{}"}' END AS info
          FROM dj_workers WHERE seen_at>=@fresh ORDER BY seen_at DESC,id LIMIT 501)
        SELECT SUBSTRING(r.id,1,512),r.seen_at,COALESCE({Integer("r.info", "$.capacity")},0),COALESCE({Integer("r.info", "$.running")},0),
          COALESCE(c.draining,0),
          CASE WHEN JSON_CONTAINS(JSON_EXTRACT(r.info,'$.processors'),JSON_OBJECT('key',j.job_key,'version',j.contract_version))
            AND JSON_CONTAINS(JSON_EXTRACT(r.info,'$.queues'),JSON_QUOTE(j.queue)) THEN 1 ELSE 0 END,
          CASE WHEN JSON_TYPE(JSON_EXTRACT(r.info,'$.processors'))='ARRAY' AND JSON_TYPE(JSON_EXTRACT(r.info,'$.queues'))='ARRAY'
            AND {Integer("r.info", "$.capacity")} IS NOT NULL AND {Integer("r.info", "$.running")} IS NOT NULL THEN 1 ELSE 0 END,
          CASE WHEN CHAR_LENGTH(r.id)>512 THEN 1 ELSE 0 END,{Integer("r.info", "$.acquisition.consecutiveFailures")}
        FROM recent r JOIN dj_jobs j ON j.id=@id LEFT JOIN dj_worker_controls c ON c.worker_id=r.id
        ORDER BY r.seen_at DESC,r.id
        """;

    /// <summary>An integer property of a JSON document, or null when it is missing or not an integer.</summary>
    private static string Integer(string document, string path) =>
        $"(CASE WHEN JSON_TYPE(JSON_EXTRACT({document},'{path}')) IN ('INTEGER','UNSIGNED INTEGER') THEN CAST(JSON_EXTRACT({document},'{path}') AS SIGNED) END)";
}
