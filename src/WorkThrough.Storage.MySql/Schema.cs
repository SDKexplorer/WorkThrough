using System.Security.Cryptography;
using System.Text;

namespace WorkThrough.Storage;

public sealed partial class MySqlJobStore
{
    /// <summary>
    /// One idempotent migration step. A step with a column adds that column to table <paramref name="Name"/>. <c>{table}</c> stands for
    /// the server's table options, so identities compare as exact binary strings on MySQL and MariaDB alike.
    /// </summary>
    private sealed record SchemaStep(string Name, bool Index, string Sql, string? Column = null);

    /// <summary>Schema 10, the first MySQL schema. Never edit or reorder: its fingerprint identifies existing installations.</summary>
    private static readonly SchemaStep[] MySqlBaseSchema =
    [
        new("dj_admission", false, "CREATE TABLE dj_admission(id INT PRIMARY KEY) {table}"),
        new("dj_retention_policy", false, "CREATE TABLE dj_retention_policy(id INT PRIMARY KEY,revision INT NOT NULL,policy LONGTEXT NOT NULL) {table}"),
        new("dj_retention_holds", false, "CREATE TABLE dj_retention_holds(unit_id VARCHAR(36) PRIMARY KEY,held INT NOT NULL,revision INT NOT NULL) {table}"),
        new("dj_retention_incidents", false, "CREATE TABLE dj_retention_incidents(unit_id VARCHAR(36) PRIMARY KEY,code LONGTEXT NOT NULL,at BIGINT NOT NULL) {table}"),
        new("dj_maintenance", false, """
            CREATE TABLE dj_maintenance(id INT PRIMARY KEY,batch_id VARCHAR(36),token VARCHAR(36),owner LONGTEXT,lease_expires_at BIGINT,
            last_batch_id VARCHAR(36),failure_code LONGTEXT,failed_at BIGINT) {table}
            """),
        new("dj_maintenance_batches", false, "CREATE TABLE dj_maintenance_batches(id VARCHAR(36) PRIMARY KEY,descriptor LONGTEXT NOT NULL,result LONGTEXT) {table}"),
        new("dj_archive_exports", false, """
            CREATE TABLE dj_archive_exports(archive_id VARCHAR(36) NOT NULL,destination VARCHAR(100) NOT NULL,sha256 VARCHAR(64) NOT NULL,
            object_version LONGTEXT NOT NULL,bytes BIGINT NOT NULL,verified_at BIGINT NOT NULL,batch_id VARCHAR(36) NOT NULL,
            PRIMARY KEY(archive_id,destination)) {table}
            """),
        new("dj_signals", false, """
            CREATE TABLE dj_signals(run_id VARCHAR(36) NOT NULL,name VARCHAR(400) NOT NULL,payload LONGTEXT NOT NULL,received_at BIGINT NOT NULL,
            PRIMARY KEY(run_id,name)) {table}
            """),
        new("dj_signal_receipts", false, """
            CREATE TABLE dj_signal_receipts(run_id VARCHAR(36) NOT NULL,idempotency_key VARCHAR(400) NOT NULL,fingerprint VARCHAR(64) NOT NULL,
            PRIMARY KEY(run_id,idempotency_key)) {table}
            """),
        new("dj_signal_waits", false, """
            CREATE TABLE dj_signal_waits(job_id VARCHAR(36) PRIMARY KEY,run_id VARCHAR(36) NOT NULL,name VARCHAR(400) NOT NULL,deadline BIGINT NOT NULL,
            INDEX ix_dj_waits_due(deadline,run_id)) {table}
            """),
        new("dj_archives", false, """
            CREATE TABLE dj_archives(id VARCHAR(36) PRIMARY KEY,kind VARCHAR(32) NOT NULL,correlation_id VARCHAR(2000),archived_at BIGINT NOT NULL,
            jobs INT NOT NULL,sha256 VARCHAR(64) NOT NULL,body LONGTEXT,INDEX ix_dj_archives_history(archived_at DESC,id)) {table}
            """),
        new("dj_job_receipts", false, """
            CREATE TABLE dj_job_receipts(idempotency_key VARCHAR(400) PRIMARY KEY,fingerprint VARCHAR(64) NOT NULL,job_id VARCHAR(36) NOT NULL) {table}
            """),
        new("dj_queue_controls", false, """
            CREATE TABLE dj_queue_controls(queue VARCHAR(512) PRIMARY KEY,paused INT NOT NULL,revision INT NOT NULL,weight INT NOT NULL DEFAULT 1) {table}
            """),
        new("dj_queue_turns", false, "CREATE TABLE dj_queue_turns(queue VARCHAR(512) PRIMARY KEY,pass BIGINT NOT NULL) {table}"),
        new("dj_queue_clock", false, "CREATE TABLE dj_queue_clock(id INT PRIMARY KEY,value BIGINT NOT NULL) {table}"),
        new("dj_keyed_limits", false, """
            CREATE TABLE dj_keyed_limits(job_key VARCHAR(120) NOT NULL,key_scope INT NOT NULL,maximum INT NOT NULL CHECK(maximum>=0),
            PRIMARY KEY(job_key,key_scope)) {table}
            """),
        new("dj_rate_limits", false, """
            CREATE TABLE dj_rate_limits(job_key VARCHAR(120) NOT NULL,key_scope INT NOT NULL,permits INT NOT NULL CHECK(permits>0),
            window_ms BIGINT NOT NULL CHECK(window_ms>0),PRIMARY KEY(job_key,key_scope)) {table}
            """),
        new("dj_rate_ledger", false, """
            CREATE TABLE dj_rate_ledger(job_key VARCHAR(120) NOT NULL,key_scope INT NOT NULL,rate_key VARCHAR(256) NOT NULL,started_at BIGINT NOT NULL,
            job_id VARCHAR(36) NOT NULL,INDEX ix_dj_rate_ledger(job_key,key_scope,rate_key,started_at),INDEX ix_dj_rate_ledger_time(started_at)) {table}
            """),
        new("dj_worker_controls", false, """
            CREATE TABLE dj_worker_controls(worker_id VARCHAR(400) PRIMARY KEY,draining INT NOT NULL,revision INT NOT NULL) {table}
            """),
        new("dj_outbox", false, """
            CREATE TABLE dj_outbox(id VARCHAR(36) PRIMARY KEY,request LONGTEXT NOT NULL,status VARCHAR(32) NOT NULL,lease_token VARCHAR(36),
            lease_expires_at BIGINT,idempotency_key VARCHAR(400) UNIQUE,fingerprint VARCHAR(64) NOT NULL,delivered_job_id VARCHAR(36)) {table}
            """),
        new("dj_store", false, "CREATE TABLE dj_store(id VARCHAR(36) PRIMARY KEY) {table}"),
        new("dj_event_sequence", false, "CREATE TABLE dj_event_sequence(id INT PRIMARY KEY,value BIGINT NOT NULL,floor BIGINT NOT NULL DEFAULT 0) {table}"),
        new("dj_subscriptions", false, "CREATE TABLE dj_subscriptions(name VARCHAR(400) PRIMARY KEY,sequence BIGINT NOT NULL) {table}"),
        new("dj_registry", false, "CREATE TABLE dj_registry(id INT PRIMARY KEY,revision BIGINT NOT NULL) {table}"),
        new("dj_compatibility", false, "CREATE TABLE dj_compatibility(id INT PRIMARY KEY,reader_minimum INT NOT NULL,protocol INT NOT NULL) {table}"),
        new("dj_limit_locks", false, "CREATE TABLE dj_limit_locks(id INT PRIMARY KEY) {table}"),
        new("dj_processor_aliases", false, """
            CREATE TABLE dj_processor_aliases(alias_key VARCHAR(120) NOT NULL,alias_version INT NOT NULL,target_key VARCHAR(120) NOT NULL,
            target_version INT NOT NULL,PRIMARY KEY(alias_key,alias_version)) {table}
            """),
        new("dj_processor_groups", false, "CREATE TABLE dj_processor_groups(job_key VARCHAR(120) PRIMARY KEY,group_key VARCHAR(120) NOT NULL) {table}"),
        new("dj_jobs", false, """
            CREATE TABLE dj_jobs(
            id VARCHAR(36) PRIMARY KEY,job_key VARCHAR(120) NOT NULL,contract_version INT NOT NULL,
            payload LONGTEXT NOT NULL,queue VARCHAR(512) NOT NULL,correlation_id VARCHAR(2000),
            status VARCHAR(32) NOT NULL,attempt INT NOT NULL,max_attempts INT NOT NULL,
            created_at BIGINT NOT NULL,available_at BIGINT NOT NULL,
            idempotency_key VARCHAR(400) UNIQUE,workflow_run_id VARCHAR(36),step_id VARCHAR(400),
            output LONGTEXT,error LONGTEXT,worker_id VARCHAR(400),lease_token VARCHAR(36),lease_expires_at BIGINT,expires_at BIGINT,
            causation_job_id VARCHAR(36),terminal_at BIGINT,partition_key VARCHAR(256),tenant_id VARCHAR(256),trace_parent VARCHAR(64),
            original_payload LONGTEXT,
            UNIQUE(workflow_run_id,step_id),
            INDEX ix_dj_jobs_ready(status,available_at,created_at),
            INDEX ix_dj_jobs_active(job_key,status,lease_expires_at),
            INDEX ix_dj_jobs_history(created_at DESC,id),
            INDEX ix_dj_jobs_processor_history(job_key,contract_version,created_at DESC,id),
            INDEX ix_dj_jobs_queue_history(queue,created_at DESC,id),
            INDEX ix_dj_jobs_state_history(status,created_at DESC,id),
            INDEX ix_dj_jobs_terminal(status,terminal_at,id),
            INDEX ix_dj_jobs_partition(partition_key,status,lease_expires_at),
            INDEX ix_dj_jobs_tenant(tenant_id,status,lease_expires_at)) {table}
            """),
        new("dj_attempts", false, """
            CREATE TABLE dj_attempts(
            job_id VARCHAR(36) NOT NULL,number INT NOT NULL,worker_id VARCHAR(400) NOT NULL,
            started_at BIGINT NOT NULL,finished_at BIGINT,status VARCHAR(32) NOT NULL,error LONGTEXT,handler LONGTEXT,
            PRIMARY KEY(job_id,number),INDEX ix_dj_attempts_failure_time(finished_at,job_id),INDEX ix_dj_attempts_finished(finished_at)) {table}
            """),
        new("dj_limits", false, "CREATE TABLE dj_limits(job_key VARCHAR(120) PRIMARY KEY,maximum INT NOT NULL CHECK(maximum>=0)) {table}"),
        new("dj_definitions", false, """
            CREATE TABLE dj_definitions(flow_key VARCHAR(120) NOT NULL,version INT NOT NULL,definition LONGTEXT NOT NULL,PRIMARY KEY(flow_key,version)) {table}
            """),
        new("dj_flows", false, """
            CREATE TABLE dj_flows(
            id VARCHAR(36) PRIMARY KEY,flow_key VARCHAR(120) NOT NULL,version INT NOT NULL,
            input LONGTEXT NOT NULL,correlation_id VARCHAR(2000),status VARCHAR(32) NOT NULL,error LONGTEXT,max_parallel INT CHECK(max_parallel>0),
            parent_run_id VARCHAR(36),parent_job_id VARCHAR(36),depth INT,forked_from_run_id VARCHAR(36),
            INDEX ix_dj_flows_bounded(max_parallel,status),INDEX ix_dj_flows_parent(parent_run_id)) {table}
            """),
        new("dj_flow_starts", false, """
            CREATE TABLE dj_flow_starts(idempotency_key VARCHAR(400) PRIMARY KEY,fingerprint VARCHAR(64) NOT NULL,run_id VARCHAR(36) NOT NULL UNIQUE) {table}
            """),
        new("dj_definition_lifecycle", false, """
            CREATE TABLE dj_definition_lifecycle(flow_key VARCHAR(120) NOT NULL,version INT NOT NULL,definition LONGTEXT NOT NULL,status VARCHAR(32) NOT NULL,
            revision INT NOT NULL,PRIMARY KEY(flow_key,version)) {table}
            """),
        new("dj_commands", false, "CREATE TABLE dj_commands(id VARCHAR(36) PRIMARY KEY,fingerprint VARCHAR(64) NOT NULL,result LONGTEXT NOT NULL) {table}"),
        new("dj_audit", false, """
            CREATE TABLE dj_audit(command_id VARCHAR(36) PRIMARY KEY,at BIGINT NOT NULL,actor LONGTEXT NOT NULL,action VARCHAR(120) NOT NULL,
            target VARCHAR(400) NOT NULL,reason LONGTEXT NOT NULL,outcome LONGTEXT NOT NULL,INDEX ix_dj_audit_history(at DESC,command_id),
            INDEX ix_dj_audit_target_history(target,at DESC,command_id),INDEX ix_dj_audit_action_history(action,at DESC,command_id)) {table}
            """),
        new("dj_audit_changes", false, "CREATE TABLE dj_audit_changes(command_id VARCHAR(36) PRIMARY KEY,changes LONGTEXT NOT NULL) {table}"),
        new("dj_failures", false, """
            CREATE TABLE dj_failures(job_id VARCHAR(36) NOT NULL,attempt INT NOT NULL,exception_type LONGTEXT NOT NULL,message LONGTEXT NOT NULL,
            stack_trace LONGTEXT,PRIMARY KEY(job_id,attempt)) {table}
            """),
        new("dj_progress", false, """
            CREATE TABLE dj_progress(job_id VARCHAR(36) PRIMARY KEY,lease_token VARCHAR(36) NOT NULL,started_at BIGINT NOT NULL,last_progress_at BIGINT NOT NULL,
            deadline BIGINT,progress_timeout BIGINT,description LONGTEXT) {table}
            """),
        new("dj_workers", false, """
            CREATE TABLE dj_workers(id VARCHAR(400) PRIMARY KEY,seen_at BIGINT NOT NULL,details LONGTEXT NOT NULL,INDEX ix_dj_workers_seen(seen_at DESC,id)) {table}
            """),
        new("dj_events", false, """
            CREATE TABLE dj_events(sequence BIGINT PRIMARY KEY,job_id VARCHAR(36) NOT NULL,run_id VARCHAR(36),at BIGINT NOT NULL,kind VARCHAR(32) NOT NULL,
            attempt INT NOT NULL,INDEX ix_dj_events_run(run_id,sequence)) {table}
            """),
        new("dj_schedules", false, """
            CREATE TABLE dj_schedules(schedule_key VARCHAR(120) NOT NULL,version INT NOT NULL,definition LONGTEXT NOT NULL,status VARCHAR(32) NOT NULL,
            revision INT NOT NULL,enabled INT NOT NULL,next_due BIGINT,checked_at BIGINT NOT NULL DEFAULT 0,PRIMARY KEY(schedule_key,version),
            INDEX ix_dj_schedules_due(enabled,status,next_due)) {table}
            """),
        new("dj_schedule_occurrences", false, """
            CREATE TABLE dj_schedule_occurrences(schedule_key VARCHAR(120) NOT NULL,version INT NOT NULL,due BIGINT NOT NULL,outcome LONGTEXT NOT NULL,
            job_id VARCHAR(36),PRIMARY KEY(schedule_key,version,due)) {table}
            """),
    ];

    /// <summary>Schema 11 additions: named calendars (ADR-032).</summary>
    private static readonly SchemaStep[] MySqlCalendarSchema =
    [
        new("dj_calendars", false, "CREATE TABLE dj_calendars(name VARCHAR(120) PRIMARY KEY,definition LONGTEXT NOT NULL,revision INT NOT NULL) {table}"),
    ];

    private static readonly SchemaStep[] MySqlSchemaSteps = [.. MySqlBaseSchema, .. MySqlCalendarSchema];

    /// <summary>Identifies the installed step list; any edit to a step's SQL changes it.</summary>
    private static readonly string SchemaFingerprint = Fingerprint(MySqlSchemaSteps);

    /// <summary>Completed earlier installations that this schema extends by resuming after their last step.</summary>
    private static readonly (int Version, int Steps, string Fingerprint)[] Predecessors =
    [
        (10, MySqlBaseSchema.Length, Fingerprint(MySqlBaseSchema))
    ];

    private static string Fingerprint(IEnumerable<SchemaStep> steps) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", steps.Select(step => step.Sql.ReplaceLineEndings("\n"))))));
}
