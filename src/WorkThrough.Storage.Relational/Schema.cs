namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS dj_jobs (
          id TEXT PRIMARY KEY, job_key TEXT NOT NULL, contract_version INTEGER NOT NULL,
          payload TEXT NOT NULL, queue TEXT NOT NULL, correlation_id TEXT,
          status TEXT NOT NULL, attempt INTEGER NOT NULL, max_attempts INTEGER NOT NULL,
          created_at BIGINT NOT NULL, available_at BIGINT NOT NULL,
          idempotency_key TEXT UNIQUE, workflow_run_id TEXT, step_id TEXT,
          output TEXT, error TEXT, worker_id TEXT, lease_token TEXT, lease_expires_at BIGINT,
          UNIQUE(workflow_run_id,step_id));
        CREATE INDEX IF NOT EXISTS ix_dj_jobs_ready ON dj_jobs(status,available_at,created_at);
        CREATE INDEX IF NOT EXISTS ix_dj_jobs_correlation ON dj_jobs(correlation_id,created_at);
        CREATE INDEX IF NOT EXISTS ix_dj_jobs_active ON dj_jobs(job_key,status,lease_expires_at);
        CREATE TABLE IF NOT EXISTS dj_attempts (
          job_id TEXT NOT NULL,number INTEGER NOT NULL,worker_id TEXT NOT NULL,
          started_at BIGINT NOT NULL,finished_at BIGINT,status TEXT NOT NULL,error TEXT,handler TEXT,
          PRIMARY KEY(job_id,number));
        CREATE TABLE IF NOT EXISTS dj_limits (job_key TEXT PRIMARY KEY,maximum INTEGER NOT NULL CHECK(maximum>=0));
        CREATE TABLE IF NOT EXISTS dj_definitions (
          flow_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,
          PRIMARY KEY(flow_key,version));
        CREATE TABLE IF NOT EXISTS dj_flows (
          id TEXT PRIMARY KEY,flow_key TEXT NOT NULL,version INTEGER NOT NULL,
          input TEXT NOT NULL,correlation_id TEXT,status TEXT NOT NULL,error TEXT);
        CREATE INDEX IF NOT EXISTS ix_dj_flows_correlation ON dj_flows(correlation_id);
        CREATE TABLE IF NOT EXISTS dj_definition_lifecycle (
          flow_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,status TEXT NOT NULL,revision INTEGER NOT NULL,
          PRIMARY KEY(flow_key,version));
        CREATE TABLE IF NOT EXISTS dj_commands (id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,result TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS dj_audit (
          command_id TEXT PRIMARY KEY,at BIGINT NOT NULL,actor TEXT NOT NULL,action TEXT NOT NULL,target TEXT NOT NULL,reason TEXT NOT NULL,outcome TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS dj_audit_changes (command_id TEXT PRIMARY KEY,changes TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS dj_failures (
          job_id TEXT NOT NULL,attempt INTEGER NOT NULL,exception_type TEXT NOT NULL,message TEXT NOT NULL,stack_trace TEXT,
          PRIMARY KEY(job_id,attempt));
        CREATE TABLE IF NOT EXISTS dj_progress (
          job_id TEXT PRIMARY KEY,lease_token TEXT NOT NULL,started_at BIGINT NOT NULL,last_progress_at BIGINT NOT NULL,
          deadline BIGINT,progress_timeout BIGINT,description TEXT);
        CREATE TABLE IF NOT EXISTS dj_workers (id TEXT PRIMARY KEY,seen_at BIGINT NOT NULL,details TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS dj_events (
          sequence BIGINT PRIMARY KEY,job_id TEXT NOT NULL,run_id TEXT,at BIGINT NOT NULL,kind TEXT NOT NULL,attempt INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_dj_events_run ON dj_events(run_id,sequence);
        CREATE TABLE IF NOT EXISTS dj_schedules (
          schedule_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,status TEXT NOT NULL,revision INTEGER NOT NULL,
          enabled INTEGER NOT NULL,next_due BIGINT,checked_at BIGINT NOT NULL DEFAULT 0,PRIMARY KEY(schedule_key,version));
        CREATE INDEX IF NOT EXISTS ix_dj_schedules_due ON dj_schedules(enabled,status,next_due);
        CREATE TABLE IF NOT EXISTS dj_schedule_occurrences (
          schedule_key TEXT NOT NULL,version INTEGER NOT NULL,due BIGINT NOT NULL,outcome TEXT NOT NULL,job_id TEXT,
          PRIMARY KEY(schedule_key,version,due));
        """;
}
