-- Schema 4 as created by develop at cab5d8a (src/WorkThrough.Storage.Relational/Schema.cs), plus one queued job.
CREATE TABLE IF NOT EXISTS dj_retention_policy (id INTEGER PRIMARY KEY,revision INTEGER NOT NULL,policy TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_retention_holds (unit_id TEXT PRIMARY KEY,held INTEGER NOT NULL,revision INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS dj_retention_incidents (unit_id TEXT PRIMARY KEY,code TEXT NOT NULL,at BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_maintenance (id INTEGER PRIMARY KEY,batch_id TEXT,token TEXT,owner TEXT,lease_expires_at BIGINT,last_batch_id TEXT,failure_code TEXT,failed_at BIGINT);
INSERT INTO dj_maintenance(id) VALUES (1) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS dj_maintenance_batches (id TEXT PRIMARY KEY,descriptor TEXT NOT NULL,result TEXT);
CREATE TABLE IF NOT EXISTS dj_archive_exports (archive_id TEXT NOT NULL,destination TEXT NOT NULL,sha256 TEXT NOT NULL,object_version TEXT NOT NULL,bytes BIGINT NOT NULL,verified_at BIGINT NOT NULL,batch_id TEXT NOT NULL,PRIMARY KEY(archive_id,destination));
CREATE TABLE IF NOT EXISTS dj_signals (run_id TEXT NOT NULL,name TEXT NOT NULL,payload TEXT NOT NULL,received_at BIGINT NOT NULL,PRIMARY KEY(run_id,name));
CREATE TABLE IF NOT EXISTS dj_signal_receipts (run_id TEXT NOT NULL,idempotency_key TEXT NOT NULL,fingerprint TEXT NOT NULL,PRIMARY KEY(run_id,idempotency_key));
CREATE TABLE IF NOT EXISTS dj_signal_waits (job_id TEXT PRIMARY KEY,run_id TEXT NOT NULL,name TEXT NOT NULL,deadline BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_dj_waits_due ON dj_signal_waits(deadline,run_id);
CREATE TABLE IF NOT EXISTS dj_archives (id TEXT PRIMARY KEY,kind TEXT NOT NULL,correlation_id TEXT,archived_at BIGINT NOT NULL,jobs INTEGER NOT NULL,sha256 TEXT NOT NULL,body TEXT);
CREATE INDEX IF NOT EXISTS ix_dj_archives_correlation ON dj_archives(correlation_id,archived_at);
CREATE INDEX IF NOT EXISTS ix_dj_archives_history ON dj_archives(archived_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_dj_archives_correlation_history ON dj_archives(correlation_id,archived_at DESC,id);
CREATE TABLE IF NOT EXISTS dj_job_receipts (idempotency_key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, job_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_queue_controls (queue TEXT PRIMARY KEY,paused INTEGER NOT NULL,revision INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS dj_worker_controls (worker_id TEXT PRIMARY KEY,draining INTEGER NOT NULL,revision INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS dj_outbox (id TEXT PRIMARY KEY,request TEXT NOT NULL,status TEXT NOT NULL,lease_token TEXT,lease_expires_at BIGINT,idempotency_key TEXT UNIQUE,fingerprint TEXT NOT NULL,delivered_job_id TEXT);
CREATE TABLE IF NOT EXISTS dj_store (id TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS dj_event_sequence (id INTEGER PRIMARY KEY,value BIGINT NOT NULL,floor BIGINT NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS dj_subscriptions (name TEXT PRIMARY KEY,sequence BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_registry (id INTEGER PRIMARY KEY,revision BIGINT NOT NULL);
INSERT INTO dj_registry(id,revision) VALUES (1,0) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS dj_processor_aliases (
  alias_key TEXT NOT NULL,alias_version INTEGER NOT NULL,target_key TEXT NOT NULL,target_version INTEGER NOT NULL,
  PRIMARY KEY(alias_key,alias_version));
CREATE TABLE IF NOT EXISTS dj_processor_groups (job_key TEXT PRIMARY KEY,group_key TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_jobs (
  id TEXT PRIMARY KEY, job_key TEXT NOT NULL, contract_version INTEGER NOT NULL,
  payload TEXT NOT NULL, queue TEXT NOT NULL, correlation_id TEXT,
  status TEXT NOT NULL, attempt INTEGER NOT NULL, max_attempts INTEGER NOT NULL,
  created_at BIGINT NOT NULL, available_at BIGINT NOT NULL,
  idempotency_key TEXT UNIQUE, workflow_run_id TEXT, step_id TEXT,
  output TEXT, error TEXT, worker_id TEXT, lease_token TEXT, lease_expires_at BIGINT, expires_at BIGINT, causation_job_id TEXT, terminal_at BIGINT,
  UNIQUE(workflow_run_id,step_id));
CREATE INDEX IF NOT EXISTS ix_dj_jobs_ready ON dj_jobs(status,available_at,created_at);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_correlation ON dj_jobs(correlation_id,created_at);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_active ON dj_jobs(job_key,status,lease_expires_at);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_history ON dj_jobs(created_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_processor_history ON dj_jobs(job_key,contract_version,created_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_queue_history ON dj_jobs(queue,created_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_state_history ON dj_jobs(status,created_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_correlation_history ON dj_jobs(correlation_id,created_at DESC,id);
CREATE TABLE IF NOT EXISTS dj_attempts (
  job_id TEXT NOT NULL,number INTEGER NOT NULL,worker_id TEXT NOT NULL,
  started_at BIGINT NOT NULL,finished_at BIGINT,status TEXT NOT NULL,error TEXT,handler TEXT,
  PRIMARY KEY(job_id,number));
CREATE INDEX IF NOT EXISTS ix_dj_attempts_failure_time ON dj_attempts(finished_at,job_id) WHERE status='Failed';
CREATE INDEX IF NOT EXISTS ix_dj_attempts_finished ON dj_attempts(finished_at);
CREATE TABLE IF NOT EXISTS dj_limits (job_key TEXT PRIMARY KEY,maximum INTEGER NOT NULL CHECK(maximum>=0));
CREATE TABLE IF NOT EXISTS dj_definitions (
  flow_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,
  PRIMARY KEY(flow_key,version));
CREATE TABLE IF NOT EXISTS dj_flows (
  id TEXT PRIMARY KEY,flow_key TEXT NOT NULL,version INTEGER NOT NULL,
  input TEXT NOT NULL,correlation_id TEXT,status TEXT NOT NULL,error TEXT);
CREATE INDEX IF NOT EXISTS ix_dj_flows_correlation ON dj_flows(correlation_id);
CREATE TABLE IF NOT EXISTS dj_flow_starts (
  idempotency_key TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,run_id TEXT NOT NULL UNIQUE);
CREATE TABLE IF NOT EXISTS dj_definition_lifecycle (
  flow_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,status TEXT NOT NULL,revision INTEGER NOT NULL,
  PRIMARY KEY(flow_key,version));
CREATE TABLE IF NOT EXISTS dj_commands (id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,result TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_audit (
  command_id TEXT PRIMARY KEY,at BIGINT NOT NULL,actor TEXT NOT NULL,action TEXT NOT NULL,target TEXT NOT NULL,reason TEXT NOT NULL,outcome TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS dj_audit_changes (command_id TEXT PRIMARY KEY,changes TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_dj_audit_history ON dj_audit(at DESC,command_id);
CREATE INDEX IF NOT EXISTS ix_dj_audit_target_history ON dj_audit(target,at DESC,command_id);
CREATE INDEX IF NOT EXISTS ix_dj_audit_action_history ON dj_audit(action,at DESC,command_id);
CREATE TABLE IF NOT EXISTS dj_failures (
  job_id TEXT NOT NULL,attempt INTEGER NOT NULL,exception_type TEXT NOT NULL,message TEXT NOT NULL,stack_trace TEXT,
  PRIMARY KEY(job_id,attempt));
CREATE TABLE IF NOT EXISTS dj_progress (
  job_id TEXT PRIMARY KEY,lease_token TEXT NOT NULL,started_at BIGINT NOT NULL,last_progress_at BIGINT NOT NULL,
  deadline BIGINT,progress_timeout BIGINT,description TEXT);
CREATE TABLE IF NOT EXISTS dj_workers (id TEXT PRIMARY KEY,seen_at BIGINT NOT NULL,details TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_dj_workers_seen ON dj_workers(seen_at DESC,id);
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
CREATE TABLE IF NOT EXISTS dj_schema (version INTEGER NOT NULL PRIMARY KEY);
CREATE INDEX IF NOT EXISTS ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id);
INSERT INTO dj_event_sequence(id,value) VALUES (1,0);
INSERT INTO dj_store(id) VALUES ('00000000000000000000000000000004');
INSERT INTO dj_schema(version) VALUES (4);
INSERT INTO dj_queue_controls(queue,paused,revision) VALUES ('default',0,2);
INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,status,attempt,max_attempts,created_at,available_at)
VALUES ('44444444-4444-4444-4444-444444444444','tests.alpha',1,'{"value":"schema-four"}','default','Queued',0,3,1,1);
