using System.Security.Cryptography;
using System.Text;

namespace WorkThrough.Storage;

public sealed partial class OracleJobStore
{
    /// <summary>One idempotent migration step. A step with a column adds that column to table <paramref name="Name"/>.</summary>
    private sealed record SchemaStep(string Name, bool Index, string Sql, string? Column = null);

    /// <summary>Schema 4. Never edit or reorder: its fingerprint identifies existing installations.</summary>
    private static readonly SchemaStep[] OracleSchema =
    [
        new("DJ_ADMISSION", false, """
            CREATE TABLE dj_admission(id NUMBER(10) PRIMARY KEY) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_RETENTION_POLICY", false, """
            CREATE TABLE dj_retention_policy (id NUMBER(10) PRIMARY KEY,revision NUMBER(10) NOT NULL,policy CLOB NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_RETENTION_HOLDS", false, """
            CREATE TABLE dj_retention_holds (unit_id VARCHAR2(36 CHAR) PRIMARY KEY,held NUMBER(10) NOT NULL,revision NUMBER(10) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_RETENTION_INCIDENTS", false, """
            CREATE TABLE dj_retention_incidents (unit_id VARCHAR2(36 CHAR) PRIMARY KEY,code CLOB NOT NULL,at NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_MAINTENANCE", false, """
            CREATE TABLE dj_maintenance (id NUMBER(10) PRIMARY KEY,batch_id VARCHAR2(36 CHAR),token VARCHAR2(36 CHAR),owner CLOB,lease_expires_at NUMBER(19),last_batch_id VARCHAR2(36 CHAR),failure_code CLOB,failed_at NUMBER(19)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_MAINTENANCE_BATCHES", false, """
            CREATE TABLE dj_maintenance_batches (id VARCHAR2(36 CHAR) PRIMARY KEY,descriptor CLOB NOT NULL,result CLOB) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_ARCHIVE_EXPORTS", false, """
            CREATE TABLE dj_archive_exports (archive_id VARCHAR2(36 CHAR) NOT NULL,destination VARCHAR2(100 CHAR) NOT NULL,sha256 VARCHAR2(64 CHAR) NOT NULL,object_version CLOB NOT NULL,bytes NUMBER(19) NOT NULL,verified_at NUMBER(19) NOT NULL,batch_id VARCHAR2(36 CHAR) NOT NULL,PRIMARY KEY(archive_id,destination)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_SIGNALS", false, """
            CREATE TABLE dj_signals (run_id VARCHAR2(36 CHAR) NOT NULL,name VARCHAR2(400 CHAR) NOT NULL,payload CLOB NOT NULL,received_at NUMBER(19) NOT NULL,PRIMARY KEY(run_id,name)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_SIGNAL_RECEIPTS", false, """
            CREATE TABLE dj_signal_receipts (run_id VARCHAR2(36 CHAR) NOT NULL,idempotency_key VARCHAR2(400 CHAR) NOT NULL,fingerprint VARCHAR2(64 CHAR) NOT NULL,PRIMARY KEY(run_id,idempotency_key)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_SIGNAL_WAITS", false, """
            CREATE TABLE dj_signal_waits (job_id VARCHAR2(36 CHAR) PRIMARY KEY,run_id VARCHAR2(36 CHAR) NOT NULL,name VARCHAR2(400 CHAR) NOT NULL,deadline NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_WAITS_DUE", true, """
            CREATE INDEX ix_dj_waits_due ON dj_signal_waits(deadline,run_id)
            """),
        new("DJ_ARCHIVES", false, """
            CREATE TABLE dj_archives (id VARCHAR2(36 CHAR) PRIMARY KEY,kind VARCHAR2(32 CHAR) NOT NULL,correlation_id VARCHAR2(2000 CHAR),archived_at NUMBER(19) NOT NULL,jobs NUMBER(10) NOT NULL,sha256 VARCHAR2(64 CHAR) NOT NULL,body CLOB) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_ARCHIVES_HISTORY", true, """
            CREATE INDEX ix_dj_archives_history ON dj_archives(archived_at DESC,id)
            """),
        new("DJ_JOB_RECEIPTS", false, """
            CREATE TABLE dj_job_receipts (idempotency_key VARCHAR2(400 CHAR) PRIMARY KEY, fingerprint VARCHAR2(64 CHAR) NOT NULL, job_id VARCHAR2(36 CHAR) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_QUEUE_CONTROLS", false, """
            CREATE TABLE dj_queue_controls (queue VARCHAR2(512 CHAR) PRIMARY KEY,paused NUMBER(10) NOT NULL,revision NUMBER(10) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_WORKER_CONTROLS", false, """
            CREATE TABLE dj_worker_controls (worker_id VARCHAR2(400 CHAR) PRIMARY KEY,draining NUMBER(10) NOT NULL,revision NUMBER(10) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_OUTBOX", false, """
            CREATE TABLE dj_outbox (id VARCHAR2(36 CHAR) PRIMARY KEY,request CLOB NOT NULL,status VARCHAR2(32 CHAR) NOT NULL,lease_token VARCHAR2(36 CHAR),lease_expires_at NUMBER(19),idempotency_key VARCHAR2(400 CHAR) UNIQUE,fingerprint VARCHAR2(64 CHAR) NOT NULL,delivered_job_id VARCHAR2(36 CHAR)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_STORE", false, """
            CREATE TABLE dj_store (id VARCHAR2(36 CHAR) PRIMARY KEY) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_EVENT_SEQUENCE", false, """
            CREATE TABLE dj_event_sequence (id NUMBER(10) PRIMARY KEY,value NUMBER(19) NOT NULL,floor NUMBER(19) DEFAULT 0 NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_SUBSCRIPTIONS", false, """
            CREATE TABLE dj_subscriptions (name VARCHAR2(400 CHAR) PRIMARY KEY,sequence NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_REGISTRY", false, """
            CREATE TABLE dj_registry (id NUMBER(10) PRIMARY KEY,revision NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_PROCESSOR_ALIASES", false, """
            CREATE TABLE dj_processor_aliases (
            alias_key VARCHAR2(120 CHAR) NOT NULL,alias_version NUMBER(10) NOT NULL,target_key VARCHAR2(120 CHAR) NOT NULL,target_version NUMBER(10) NOT NULL,
            PRIMARY KEY(alias_key,alias_version)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_PROCESSOR_GROUPS", false, """
            CREATE TABLE dj_processor_groups (job_key VARCHAR2(120 CHAR) PRIMARY KEY,group_key VARCHAR2(120 CHAR) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_JOBS", false, """
            CREATE TABLE dj_jobs (
            id VARCHAR2(36 CHAR) PRIMARY KEY, job_key VARCHAR2(120 CHAR) NOT NULL, contract_version NUMBER(10) NOT NULL,
            payload CLOB NOT NULL, queue VARCHAR2(512 CHAR) NOT NULL, correlation_id VARCHAR2(2000 CHAR),
            status VARCHAR2(32 CHAR) NOT NULL, attempt NUMBER(10) NOT NULL, max_attempts NUMBER(10) NOT NULL,
            created_at NUMBER(19) NOT NULL, available_at NUMBER(19) NOT NULL,
            idempotency_key VARCHAR2(400 CHAR) UNIQUE, workflow_run_id VARCHAR2(36 CHAR), step_id VARCHAR2(400 CHAR),
            output CLOB, error CLOB, worker_id VARCHAR2(400 CHAR), lease_token VARCHAR2(36 CHAR), lease_expires_at NUMBER(19), expires_at NUMBER(19), causation_job_id VARCHAR2(36 CHAR), terminal_at NUMBER(19),
            UNIQUE(workflow_run_id,step_id)) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_JOBS_READY", true, """
            CREATE INDEX ix_dj_jobs_ready ON dj_jobs(status,available_at,created_at)
            """),
        new("IX_DJ_JOBS_ACTIVE", true, """
            CREATE INDEX ix_dj_jobs_active ON dj_jobs(job_key,status,lease_expires_at)
            """),
        new("IX_DJ_JOBS_HISTORY", true, """
            CREATE INDEX ix_dj_jobs_history ON dj_jobs(created_at DESC,id)
            """),
        new("IX_DJ_JOBS_PROCESSOR_HISTORY", true, """
            CREATE INDEX ix_dj_jobs_processor_history ON dj_jobs(job_key,contract_version,created_at DESC,id)
            """),
        new("IX_DJ_JOBS_QUEUE_HISTORY", true, """
            CREATE INDEX ix_dj_jobs_queue_history ON dj_jobs(queue,created_at DESC,id)
            """),
        new("IX_DJ_JOBS_STATE_HISTORY", true, """
            CREATE INDEX ix_dj_jobs_state_history ON dj_jobs(status,created_at DESC,id)
            """),
        new("DJ_ATTEMPTS", false, """
            CREATE TABLE dj_attempts (
            job_id VARCHAR2(36 CHAR) NOT NULL,"NUMBER" NUMBER(10) NOT NULL,worker_id VARCHAR2(400 CHAR) NOT NULL,
            started_at NUMBER(19) NOT NULL,finished_at NUMBER(19),status VARCHAR2(32 CHAR) NOT NULL,error CLOB,handler CLOB,
            PRIMARY KEY(job_id,"NUMBER")) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_ATTEMPTS_FAILURE_TIME", true, """
            CREATE INDEX ix_dj_attempts_failure_time ON dj_attempts(finished_at,job_id)
            """),
        new("IX_DJ_ATTEMPTS_FINISHED", true, """
            CREATE INDEX ix_dj_attempts_finished ON dj_attempts(finished_at)
            """),
        new("DJ_LIMITS", false, """
            CREATE TABLE dj_limits (job_key VARCHAR2(120 CHAR) PRIMARY KEY,maximum NUMBER(10) NOT NULL CHECK(maximum>=0)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_DEFINITIONS", false, """
            CREATE TABLE dj_definitions (
            flow_key VARCHAR2(120 CHAR) NOT NULL,version NUMBER(10) NOT NULL,definition CLOB NOT NULL,
            PRIMARY KEY(flow_key,version)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_FLOWS", false, """
            CREATE TABLE dj_flows (
            id VARCHAR2(36 CHAR) PRIMARY KEY,flow_key VARCHAR2(120 CHAR) NOT NULL,version NUMBER(10) NOT NULL,
            input CLOB NOT NULL,correlation_id VARCHAR2(2000 CHAR),status VARCHAR2(32 CHAR) NOT NULL,error CLOB) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_FLOW_STARTS", false, """
            CREATE TABLE dj_flow_starts (
            idempotency_key VARCHAR2(400 CHAR) PRIMARY KEY,fingerprint VARCHAR2(64 CHAR) NOT NULL,run_id VARCHAR2(36 CHAR) NOT NULL UNIQUE) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_DEFINITION_LIFECYCLE", false, """
            CREATE TABLE dj_definition_lifecycle (
            flow_key VARCHAR2(120 CHAR) NOT NULL,version NUMBER(10) NOT NULL,definition CLOB NOT NULL,status VARCHAR2(32 CHAR) NOT NULL,revision NUMBER(10) NOT NULL,
            PRIMARY KEY(flow_key,version)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_COMMANDS", false, """
            CREATE TABLE dj_commands (id VARCHAR2(36 CHAR) PRIMARY KEY,fingerprint VARCHAR2(64 CHAR) NOT NULL,result CLOB NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_AUDIT", false, """
            CREATE TABLE dj_audit (
            command_id VARCHAR2(36 CHAR) PRIMARY KEY,at NUMBER(19) NOT NULL,actor CLOB NOT NULL,action VARCHAR2(120 CHAR) NOT NULL,target VARCHAR2(400 CHAR) NOT NULL,reason CLOB NOT NULL,outcome CLOB NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_AUDIT_CHANGES", false, """
            CREATE TABLE dj_audit_changes (command_id VARCHAR2(36 CHAR) PRIMARY KEY,changes CLOB NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_AUDIT_HISTORY", true, """
            CREATE INDEX ix_dj_audit_history ON dj_audit(at DESC,command_id)
            """),
        new("IX_DJ_AUDIT_TARGET_HISTORY", true, """
            CREATE INDEX ix_dj_audit_target_history ON dj_audit(target,at DESC,command_id)
            """),
        new("IX_DJ_AUDIT_ACTION_HISTORY", true, """
            CREATE INDEX ix_dj_audit_action_history ON dj_audit(action,at DESC,command_id)
            """),
        new("DJ_FAILURES", false, """
            CREATE TABLE dj_failures (
            job_id VARCHAR2(36 CHAR) NOT NULL,attempt NUMBER(10) NOT NULL,exception_type CLOB NOT NULL,message CLOB NOT NULL,stack_trace CLOB,
            PRIMARY KEY(job_id,attempt)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_PROGRESS", false, """
            CREATE TABLE dj_progress (
            job_id VARCHAR2(36 CHAR) PRIMARY KEY,lease_token VARCHAR2(36 CHAR) NOT NULL,started_at NUMBER(19) NOT NULL,last_progress_at NUMBER(19) NOT NULL,
            deadline NUMBER(19),progress_timeout NUMBER(19),description CLOB) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_WORKERS", false, """
            CREATE TABLE dj_workers (id VARCHAR2(400 CHAR) PRIMARY KEY,seen_at NUMBER(19) NOT NULL,details CLOB NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_WORKERS_SEEN", true, """
            CREATE INDEX ix_dj_workers_seen ON dj_workers(seen_at DESC,id)
            """),
        new("DJ_EVENTS", false, """
            CREATE TABLE dj_events (
            sequence NUMBER(19) PRIMARY KEY,job_id VARCHAR2(36 CHAR) NOT NULL,run_id VARCHAR2(36 CHAR),at NUMBER(19) NOT NULL,kind VARCHAR2(32 CHAR) NOT NULL,attempt NUMBER(10) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_EVENTS_RUN", true, """
            CREATE INDEX ix_dj_events_run ON dj_events(run_id,sequence)
            """),
        new("DJ_SCHEDULES", false, """
            CREATE TABLE dj_schedules (
            schedule_key VARCHAR2(120 CHAR) NOT NULL,version NUMBER(10) NOT NULL,definition CLOB NOT NULL,status VARCHAR2(32 CHAR) NOT NULL,revision NUMBER(10) NOT NULL,
            enabled NUMBER(10) NOT NULL,next_due NUMBER(19),checked_at NUMBER(19) DEFAULT 0 NOT NULL,PRIMARY KEY(schedule_key,version)) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_SCHEDULES_DUE", true, """
            CREATE INDEX ix_dj_schedules_due ON dj_schedules(enabled,status,next_due)
            """),
        new("DJ_SCHEDULE_OCCURRENCES", false, """
            CREATE TABLE dj_schedule_occurrences (
            schedule_key VARCHAR2(120 CHAR) NOT NULL,version NUMBER(10) NOT NULL,due NUMBER(19) NOT NULL,outcome CLOB NOT NULL,job_id VARCHAR2(36 CHAR),
            PRIMARY KEY(schedule_key,version,due)) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_JOBS_TERMINAL", true, """
            CREATE INDEX ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id)
            """),
    ];

    /// <summary>Schema 5 additions (ADR-018), applied after the schema-4 steps.</summary>
    private static readonly SchemaStep[] OracleAdmissionSchema =
    [
        new("DJ_JOBS", false, """
            ALTER TABLE dj_jobs ADD partition_key VARCHAR2(256 CHAR)
            """, Column: "PARTITION_KEY"),
        new("DJ_JOBS", false, """
            ALTER TABLE dj_jobs ADD tenant_id VARCHAR2(256 CHAR)
            """, Column: "TENANT_ID"),
        new("DJ_JOBS", false, """
            ALTER TABLE dj_jobs ADD trace_parent VARCHAR2(64 CHAR)
            """, Column: "TRACE_PARENT"),
        new("DJ_FLOWS", false, """
            ALTER TABLE dj_flows ADD max_parallel NUMBER(10) CHECK(max_parallel>0)
            """, Column: "MAX_PARALLEL"),
        new("DJ_QUEUE_CONTROLS", false, """
            ALTER TABLE dj_queue_controls ADD weight NUMBER(10) DEFAULT 1 NOT NULL
            """, Column: "WEIGHT"),
        new("DJ_QUEUE_TURNS", false, """
            CREATE TABLE dj_queue_turns (queue VARCHAR2(512 CHAR) PRIMARY KEY,pass NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_QUEUE_CLOCK", false, """
            CREATE TABLE dj_queue_clock (id NUMBER(10) PRIMARY KEY,value NUMBER(19) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_KEYED_LIMITS", false, """
            CREATE TABLE dj_keyed_limits (
            job_key VARCHAR2(120 CHAR) NOT NULL,key_scope NUMBER(10) NOT NULL,maximum NUMBER(10) NOT NULL CHECK(maximum>=0),
            PRIMARY KEY(job_key,key_scope)) SEGMENT CREATION IMMEDIATE
            """),
        new("DJ_RATE_LIMITS", false, """
            CREATE TABLE dj_rate_limits (
            job_key VARCHAR2(120 CHAR) NOT NULL,key_scope NUMBER(10) NOT NULL,permits NUMBER(10) NOT NULL CHECK(permits>0),
            window_ms NUMBER(19) NOT NULL CHECK(window_ms>0),PRIMARY KEY(job_key,key_scope)) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_JOBS_PARTITION", true, """
            CREATE INDEX ix_dj_jobs_partition ON dj_jobs(partition_key)
            """),
        // Oracle omits all-null keys from B-tree indexes, so work without a tenant or bound never maintains these.
        new("IX_DJ_JOBS_TENANT", true, """
            CREATE INDEX ix_dj_jobs_tenant ON dj_jobs(tenant_id)
            """),
        new("IX_DJ_FLOWS_BOUNDED", true, """
            CREATE INDEX ix_dj_flows_bounded ON dj_flows(max_parallel)
            """),
        new("DJ_RATE_LEDGER", false, """
            CREATE TABLE dj_rate_ledger (
            job_key VARCHAR2(120 CHAR) NOT NULL,key_scope NUMBER(10) NOT NULL,rate_key VARCHAR2(256 CHAR) NOT NULL,
            started_at NUMBER(19) NOT NULL,job_id VARCHAR2(36 CHAR) NOT NULL) SEGMENT CREATION IMMEDIATE
            """),
        new("IX_DJ_RATE_LEDGER", true, """
            CREATE INDEX ix_dj_rate_ledger ON dj_rate_ledger(job_key,key_scope,rate_key,started_at)
            """),
        new("IX_DJ_RATE_LEDGER_TIME", true, """
            CREATE INDEX ix_dj_rate_ledger_time ON dj_rate_ledger(started_at)
            """),
    ];

    /// <summary>Schema 6 additions (ADR-021), applied after the schema-5 steps.</summary>
    private static readonly SchemaStep[] OracleCompatibilitySchema =
    [
        new("DJ_COMPATIBILITY", false, """
            CREATE TABLE dj_compatibility (id NUMBER(10) PRIMARY KEY,reader_minimum NUMBER(10) NOT NULL,protocol NUMBER(10) NOT NULL)
            SEGMENT CREATION IMMEDIATE
            """),
    ];

    /// <summary>Schema 7 additions (ADR-022), applied after the schema-6 steps.</summary>
    private static readonly SchemaStep[] OracleLimitLockSchema =
    [
        new("DJ_LIMIT_LOCKS", false, """
            CREATE TABLE dj_limit_locks (id NUMBER(10) PRIMARY KEY) SEGMENT CREATION IMMEDIATE
            """),
    ];

    /// <summary>Schema 8 additions (ADR-023), applied after the schema-7 steps.</summary>
    private static readonly SchemaStep[] OracleChildFlowSchema =
    [
        new("DJ_FLOWS", false, """
            ALTER TABLE dj_flows ADD parent_run_id VARCHAR2(36 CHAR)
            """, Column: "PARENT_RUN_ID"),
        new("DJ_FLOWS", false, """
            ALTER TABLE dj_flows ADD parent_job_id VARCHAR2(36 CHAR)
            """, Column: "PARENT_JOB_ID"),
        new("DJ_FLOWS", false, """
            ALTER TABLE dj_flows ADD depth NUMBER(10)
            """, Column: "DEPTH"),
        new("IX_DJ_FLOWS_PARENT", true, """
            CREATE INDEX ix_dj_flows_parent ON dj_flows(parent_run_id)
            """),
    ];

    private static readonly SchemaStep[] OracleSchemaSteps =
        [.. OracleSchema, .. OracleAdmissionSchema, .. OracleCompatibilitySchema, .. OracleLimitLockSchema, .. OracleChildFlowSchema];

    /// <summary>Identifies the installed step list; any edit to a step's SQL changes it.</summary>
    private static readonly string SchemaFingerprint = Fingerprint(OracleSchemaSteps);

    /// <summary>Completed earlier installations that this schema extends by resuming after their last step.</summary>
    private static readonly (int Version, int Steps, string Fingerprint)[] Predecessors =
    [
        (4, OracleSchema.Length, Fingerprint(OracleSchema)),
        (5, OracleSchema.Length + OracleAdmissionSchema.Length, Fingerprint([.. OracleSchema, .. OracleAdmissionSchema])),
        (6, OracleSchema.Length + OracleAdmissionSchema.Length + OracleCompatibilitySchema.Length,
            Fingerprint([.. OracleSchema, .. OracleAdmissionSchema, .. OracleCompatibilitySchema])),
        (7, OracleSchema.Length + OracleAdmissionSchema.Length + OracleCompatibilitySchema.Length + OracleLimitLockSchema.Length,
            Fingerprint([.. OracleSchema, .. OracleAdmissionSchema, .. OracleCompatibilitySchema, .. OracleLimitLockSchema]))
    ];

    private static string Fingerprint(IEnumerable<SchemaStep> steps) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", steps.Select(step => step.Sql.ReplaceLineEndings("\n"))))));
}
