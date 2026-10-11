namespace WorkThrough.Storage;

public sealed partial class SqlServerJobStore
{
    private const string SqlServerSchema = """
        IF OBJECT_ID(N'dj_retention_policy',N'U') IS NULL
            CREATE TABLE dj_retention_policy (
                id int PRIMARY KEY,
                revision int NOT NULL,
                policy nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL
            );
        IF OBJECT_ID(N'dj_retention_holds',N'U') IS NULL
            CREATE TABLE dj_retention_holds (
                unit_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                held int NOT NULL,
                revision int NOT NULL,
                CHECK(DATALENGTH(unit_id)=DATALENGTH(RTRIM(unit_id)))
            );
        IF OBJECT_ID(N'dj_retention_incidents',N'U') IS NULL
            CREATE TABLE dj_retention_incidents (
                unit_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                code nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                at BIGINT NOT NULL,
                CHECK(DATALENGTH(unit_id)=DATALENGTH(RTRIM(unit_id)))
            );
        IF OBJECT_ID(N'dj_maintenance',N'U') IS NULL
            CREATE TABLE dj_maintenance (
                id int PRIMARY KEY,
                batch_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                token nvarchar(36) COLLATE Latin1_General_100_BIN2,
                owner nvarchar(max) COLLATE Latin1_General_100_BIN2,
                lease_expires_at BIGINT,
                last_batch_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                failure_code nvarchar(max) COLLATE Latin1_General_100_BIN2,
                failed_at BIGINT,
                CHECK(DATALENGTH(batch_id)=DATALENGTH(RTRIM(batch_id))),
                CHECK(DATALENGTH(token)=DATALENGTH(RTRIM(token))),
                CHECK(DATALENGTH(last_batch_id)=DATALENGTH(RTRIM(last_batch_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM dj_maintenance WHERE id=1) INSERT INTO dj_maintenance(id) VALUES (1);
        IF OBJECT_ID(N'dj_maintenance_batches',N'U') IS NULL
            CREATE TABLE dj_maintenance_batches (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                descriptor nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                result nvarchar(max) COLLATE Latin1_General_100_BIN2,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id)))
            );
        IF OBJECT_ID(N'dj_archive_exports',N'U') IS NULL
            CREATE TABLE dj_archive_exports (
                archive_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                destination nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                sha256 nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                object_version nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                bytes BIGINT NOT NULL,
                verified_at BIGINT NOT NULL,
                batch_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY(archive_id,destination),
                CHECK(DATALENGTH(archive_id)=DATALENGTH(RTRIM(archive_id))),
                CHECK(DATALENGTH(destination)=DATALENGTH(RTRIM(destination))),
                CHECK(DATALENGTH(batch_id)=DATALENGTH(RTRIM(batch_id)))
            );
        IF OBJECT_ID(N'dj_signals',N'U') IS NULL
            CREATE TABLE dj_signals (
                run_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                name nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                received_at BIGINT NOT NULL,
                PRIMARY KEY(run_id,name),
                CHECK(DATALENGTH(run_id)=DATALENGTH(RTRIM(run_id))),
                CHECK(DATALENGTH(name)=DATALENGTH(RTRIM(name)))
            );
        IF OBJECT_ID(N'dj_signal_receipts',N'U') IS NULL
            CREATE TABLE dj_signal_receipts (
                run_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                idempotency_key nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                fingerprint nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY(run_id,idempotency_key),
                CHECK(DATALENGTH(run_id)=DATALENGTH(RTRIM(run_id))),
                CHECK(DATALENGTH(idempotency_key)=DATALENGTH(RTRIM(idempotency_key)))
            );
        IF OBJECT_ID(N'dj_signal_waits',N'U') IS NULL
            CREATE TABLE dj_signal_waits (
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                run_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                name nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                deadline BIGINT NOT NULL,
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id))),
                CHECK(DATALENGTH(run_id)=DATALENGTH(RTRIM(run_id))),
                CHECK(DATALENGTH(name)=DATALENGTH(RTRIM(name)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_waits_due' AND object_id=OBJECT_ID(N'dj_signal_waits')) CREATE INDEX ix_dj_waits_due ON dj_signal_waits(deadline,run_id);
        IF OBJECT_ID(N'dj_archives',N'U') IS NULL
            CREATE TABLE dj_archives (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                kind nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                correlation_id nvarchar(max) COLLATE Latin1_General_100_BIN2,
                archived_at BIGINT NOT NULL,
                jobs int NOT NULL,
                sha256 nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                body nvarchar(max) COLLATE Latin1_General_100_BIN2,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id))),
                CHECK(DATALENGTH(correlation_id)=DATALENGTH(RTRIM(correlation_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_archives_history' AND object_id=OBJECT_ID(N'dj_archives')) CREATE INDEX ix_dj_archives_history ON dj_archives(archived_at DESC,id);
        IF OBJECT_ID(N'dj_job_receipts',N'U') IS NULL
            CREATE TABLE dj_job_receipts (
                idempotency_key nvarchar(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                fingerprint nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(idempotency_key)=DATALENGTH(RTRIM(idempotency_key))),
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id)))
            );
        IF OBJECT_ID(N'dj_queue_controls',N'U') IS NULL
            CREATE TABLE dj_queue_controls (
                queue nvarchar(512) COLLATE Latin1_General_100_BIN2 PRIMARY KEY NONCLUSTERED,
                paused int NOT NULL,
                revision int NOT NULL,
                CHECK(DATALENGTH(queue)=DATALENGTH(RTRIM(queue)))
            );
        IF OBJECT_ID(N'dj_worker_controls',N'U') IS NULL
            CREATE TABLE dj_worker_controls (
                worker_id nvarchar(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                draining int NOT NULL,
                revision int NOT NULL,
                CHECK(DATALENGTH(worker_id)=DATALENGTH(RTRIM(worker_id)))
            );
        IF OBJECT_ID(N'dj_outbox',N'U') IS NULL
            CREATE TABLE dj_outbox (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                request nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                lease_token nvarchar(36) COLLATE Latin1_General_100_BIN2,
                lease_expires_at BIGINT,
                idempotency_key nvarchar(400) COLLATE Latin1_General_100_BIN2,
                fingerprint nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                delivered_job_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id))),
                CHECK(DATALENGTH(lease_token)=DATALENGTH(RTRIM(lease_token))),
                CHECK(DATALENGTH(idempotency_key)=DATALENGTH(RTRIM(idempotency_key))),
                CHECK(DATALENGTH(delivered_job_id)=DATALENGTH(RTRIM(delivered_job_id)))
            );
        IF OBJECT_ID(N'dj_store',N'U') IS NULL
            CREATE TABLE dj_store (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id)))
            );
        IF OBJECT_ID(N'dj_event_sequence',N'U') IS NULL
            CREATE TABLE dj_event_sequence (
                id int PRIMARY KEY,
                value BIGINT NOT NULL,
                floor BIGINT NOT NULL DEFAULT 0
            );
        IF OBJECT_ID(N'dj_subscriptions',N'U') IS NULL
            CREATE TABLE dj_subscriptions (
                name nvarchar(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                sequence BIGINT NOT NULL,
                CHECK(DATALENGTH(name)=DATALENGTH(RTRIM(name)))
            );
        IF OBJECT_ID(N'dj_registry',N'U') IS NULL
            CREATE TABLE dj_registry (
                id int PRIMARY KEY,
                revision BIGINT NOT NULL
            );
        IF NOT EXISTS(SELECT 1 FROM dj_registry WHERE id=1) INSERT INTO dj_registry(id,revision) VALUES (1,0);
        IF OBJECT_ID(N'dj_compatibility',N'U') IS NULL
            CREATE TABLE dj_compatibility (
                id int PRIMARY KEY,
                reader_minimum int NOT NULL,
                protocol int NOT NULL
            );
        IF OBJECT_ID(N'dj_limit_locks',N'U') IS NULL
            CREATE TABLE dj_limit_locks (
                id int PRIMARY KEY
            );
        IF OBJECT_ID(N'dj_processor_aliases',N'U') IS NULL
            CREATE TABLE dj_processor_aliases (
                alias_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                alias_version int NOT NULL,
                target_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                target_version int NOT NULL,
                PRIMARY KEY(alias_key,alias_version),
                CHECK(DATALENGTH(alias_key)=DATALENGTH(RTRIM(alias_key))),
                CHECK(DATALENGTH(target_key)=DATALENGTH(RTRIM(target_key)))
            );
        IF OBJECT_ID(N'dj_processor_groups',N'U') IS NULL
            CREATE TABLE dj_processor_groups (
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                group_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(job_key)=DATALENGTH(RTRIM(job_key))),
                CHECK(DATALENGTH(group_key)=DATALENGTH(RTRIM(group_key)))
            );
        IF OBJECT_ID(N'dj_jobs',N'U') IS NULL
            CREATE TABLE dj_jobs (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                contract_version int NOT NULL,
                payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                queue nvarchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
                correlation_id nvarchar(max) COLLATE Latin1_General_100_BIN2,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                attempt int NOT NULL,
                max_attempts int NOT NULL,
                created_at BIGINT NOT NULL,
                available_at BIGINT NOT NULL,
                idempotency_key nvarchar(400) COLLATE Latin1_General_100_BIN2,
                workflow_run_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                step_id nvarchar(400) COLLATE Latin1_General_100_BIN2,
                output nvarchar(max) COLLATE Latin1_General_100_BIN2,
                error nvarchar(max) COLLATE Latin1_General_100_BIN2,
                worker_id nvarchar(400) COLLATE Latin1_General_100_BIN2,
                lease_token nvarchar(36) COLLATE Latin1_General_100_BIN2,
                lease_expires_at BIGINT,
                expires_at BIGINT,
                causation_job_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                terminal_at BIGINT,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id))),
                CHECK(DATALENGTH(job_key)=DATALENGTH(RTRIM(job_key))),
                CHECK(DATALENGTH(queue)=DATALENGTH(RTRIM(queue))),
                CHECK(DATALENGTH(correlation_id)=DATALENGTH(RTRIM(correlation_id))),
                CHECK(DATALENGTH(idempotency_key)=DATALENGTH(RTRIM(idempotency_key))),
                CHECK(DATALENGTH(workflow_run_id)=DATALENGTH(RTRIM(workflow_run_id))),
                CHECK(DATALENGTH(step_id)=DATALENGTH(RTRIM(step_id))),
                CHECK(DATALENGTH(worker_id)=DATALENGTH(RTRIM(worker_id))),
                CHECK(DATALENGTH(lease_token)=DATALENGTH(RTRIM(lease_token))),
                CHECK(DATALENGTH(causation_job_id)=DATALENGTH(RTRIM(causation_job_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_ready' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_ready ON dj_jobs(status,available_at,created_at);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_active' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_active ON dj_jobs(job_key,status,lease_expires_at);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_history' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_history ON dj_jobs(created_at DESC,id);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_processor_history' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_processor_history ON dj_jobs(job_key,contract_version,created_at DESC,id);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_queue_history' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_queue_history ON dj_jobs(queue,created_at DESC,id);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_state_history' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_state_history ON dj_jobs(status,created_at DESC,id);
        IF OBJECT_ID(N'dj_attempts',N'U') IS NULL
            CREATE TABLE dj_attempts (
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                number int NOT NULL,
                worker_id nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                started_at BIGINT NOT NULL,
                finished_at BIGINT,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                error nvarchar(max) COLLATE Latin1_General_100_BIN2,
                handler nvarchar(max) COLLATE Latin1_General_100_BIN2,
                PRIMARY KEY(job_id,number),
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id))),
                CHECK(DATALENGTH(worker_id)=DATALENGTH(RTRIM(worker_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_attempts_failure_time' AND object_id=OBJECT_ID(N'dj_attempts')) CREATE INDEX ix_dj_attempts_failure_time ON dj_attempts(finished_at,job_id) WHERE status='Failed';
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_attempts_finished' AND object_id=OBJECT_ID(N'dj_attempts')) CREATE INDEX ix_dj_attempts_finished ON dj_attempts(finished_at);
        IF OBJECT_ID(N'dj_limits',N'U') IS NULL
            CREATE TABLE dj_limits (
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                maximum int NOT NULL CHECK(maximum>=0),
                CHECK(DATALENGTH(job_key)=DATALENGTH(RTRIM(job_key)))
            );
        IF OBJECT_ID(N'dj_definitions',N'U') IS NULL
            CREATE TABLE dj_definitions (
                flow_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                version int NOT NULL,
                definition nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY(flow_key,version),
                CHECK(DATALENGTH(flow_key)=DATALENGTH(RTRIM(flow_key)))
            );
        IF OBJECT_ID(N'dj_flows',N'U') IS NULL
            CREATE TABLE dj_flows (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                flow_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                version int NOT NULL,
                input nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                correlation_id nvarchar(max) COLLATE Latin1_General_100_BIN2,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                error nvarchar(max) COLLATE Latin1_General_100_BIN2,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id))),
                CHECK(DATALENGTH(flow_key)=DATALENGTH(RTRIM(flow_key))),
                CHECK(DATALENGTH(correlation_id)=DATALENGTH(RTRIM(correlation_id)))
            );
        IF OBJECT_ID(N'dj_flow_starts',N'U') IS NULL
            CREATE TABLE dj_flow_starts (
                idempotency_key nvarchar(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                fingerprint nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                run_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
                CHECK(DATALENGTH(idempotency_key)=DATALENGTH(RTRIM(idempotency_key))),
                CHECK(DATALENGTH(run_id)=DATALENGTH(RTRIM(run_id)))
            );
        IF OBJECT_ID(N'dj_definition_lifecycle',N'U') IS NULL
            CREATE TABLE dj_definition_lifecycle (
                flow_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                version int NOT NULL,
                definition nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                revision int NOT NULL,
                PRIMARY KEY(flow_key,version),
                CHECK(DATALENGTH(flow_key)=DATALENGTH(RTRIM(flow_key)))
            );
        IF OBJECT_ID(N'dj_commands',N'U') IS NULL
            CREATE TABLE dj_commands (
                id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                fingerprint nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                result nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id)))
            );
        IF OBJECT_ID(N'dj_audit',N'U') IS NULL
            CREATE TABLE dj_audit (
                command_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                at BIGINT NOT NULL,
                actor nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                action nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                target nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                reason nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                outcome nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(command_id)=DATALENGTH(RTRIM(command_id))),
                CHECK(DATALENGTH(action)=DATALENGTH(RTRIM(action))),
                CHECK(DATALENGTH(target)=DATALENGTH(RTRIM(target)))
            );
        IF OBJECT_ID(N'dj_audit_changes',N'U') IS NULL
            CREATE TABLE dj_audit_changes (
                command_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                changes nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(command_id)=DATALENGTH(RTRIM(command_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_audit_history' AND object_id=OBJECT_ID(N'dj_audit')) CREATE INDEX ix_dj_audit_history ON dj_audit(at DESC,command_id);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_audit_target_history' AND object_id=OBJECT_ID(N'dj_audit')) CREATE INDEX ix_dj_audit_target_history ON dj_audit(target,at DESC,command_id);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_audit_action_history' AND object_id=OBJECT_ID(N'dj_audit')) CREATE INDEX ix_dj_audit_action_history ON dj_audit(action,at DESC,command_id);
        IF OBJECT_ID(N'dj_failures',N'U') IS NULL
            CREATE TABLE dj_failures (
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                attempt int NOT NULL,
                exception_type nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                message nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                stack_trace nvarchar(max) COLLATE Latin1_General_100_BIN2,
                PRIMARY KEY(job_id,attempt),
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id)))
            );
        IF OBJECT_ID(N'dj_progress',N'U') IS NULL
            CREATE TABLE dj_progress (
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                lease_token nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                started_at BIGINT NOT NULL,
                last_progress_at BIGINT NOT NULL,
                deadline BIGINT,
                progress_timeout BIGINT,
                description nvarchar(max) COLLATE Latin1_General_100_BIN2,
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id))),
                CHECK(DATALENGTH(lease_token)=DATALENGTH(RTRIM(lease_token)))
            );
        IF OBJECT_ID(N'dj_workers',N'U') IS NULL
            CREATE TABLE dj_workers (
                id nvarchar(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                seen_at BIGINT NOT NULL,
                details nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CHECK(DATALENGTH(id)=DATALENGTH(RTRIM(id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_workers_seen' AND object_id=OBJECT_ID(N'dj_workers')) CREATE INDEX ix_dj_workers_seen ON dj_workers(seen_at DESC,id);
        IF OBJECT_ID(N'dj_events',N'U') IS NULL
            CREATE TABLE dj_events (
                sequence BIGINT PRIMARY KEY,
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
                run_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                at BIGINT NOT NULL,
                kind nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                attempt int NOT NULL,
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id))),
                CHECK(DATALENGTH(run_id)=DATALENGTH(RTRIM(run_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_events_run' AND object_id=OBJECT_ID(N'dj_events')) CREATE INDEX ix_dj_events_run ON dj_events(run_id,sequence);
        IF OBJECT_ID(N'dj_schedules',N'U') IS NULL
            CREATE TABLE dj_schedules (
                schedule_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                version int NOT NULL,
                definition nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                status nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                revision int NOT NULL,
                enabled int NOT NULL,
                next_due BIGINT,
                checked_at BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY(schedule_key,version),
                CHECK(DATALENGTH(schedule_key)=DATALENGTH(RTRIM(schedule_key)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_schedules_due' AND object_id=OBJECT_ID(N'dj_schedules')) CREATE INDEX ix_dj_schedules_due ON dj_schedules(enabled,status,next_due);
        IF OBJECT_ID(N'dj_schedule_occurrences',N'U') IS NULL
            CREATE TABLE dj_schedule_occurrences (
                schedule_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                version int NOT NULL,
                due BIGINT NOT NULL,
                outcome nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2,
                PRIMARY KEY(schedule_key,version,due),
                CHECK(DATALENGTH(schedule_key)=DATALENGTH(RTRIM(schedule_key))),
                CHECK(DATALENGTH(job_id)=DATALENGTH(RTRIM(job_id)))
            );
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ux_dj_jobs_idempotency' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE UNIQUE INDEX ux_dj_jobs_idempotency ON dj_jobs(idempotency_key) WHERE idempotency_key IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ux_dj_outbox_idempotency' AND object_id=OBJECT_ID(N'dj_outbox')) CREATE UNIQUE INDEX ux_dj_outbox_idempotency ON dj_outbox(idempotency_key) WHERE idempotency_key IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ux_dj_jobs_step' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE UNIQUE INDEX ux_dj_jobs_step ON dj_jobs(workflow_run_id,step_id) WHERE workflow_run_id IS NOT NULL AND step_id IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_terminal' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id);
        IF NOT EXISTS(SELECT 1 FROM dj_event_sequence WHERE id=1) INSERT INTO dj_event_sequence(id,value) SELECT 1,COALESCE(MAX(sequence),0) FROM dj_events;
        """;

    /// <summary>Schema 5 additions (ADR-018), applied by name to fresh and schema-4 stores.</summary>
    private const string SqlServerAdmissionSchema = """
        IF COL_LENGTH(N'dj_jobs',N'partition_key') IS NULL
            ALTER TABLE dj_jobs ADD partition_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL
                CONSTRAINT ck_dj_jobs_partition_key CHECK(DATALENGTH(partition_key)=DATALENGTH(RTRIM(partition_key)));
        IF COL_LENGTH(N'dj_jobs',N'tenant_id') IS NULL
            ALTER TABLE dj_jobs ADD tenant_id nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL
                CONSTRAINT ck_dj_jobs_tenant_id CHECK(DATALENGTH(tenant_id)=DATALENGTH(RTRIM(tenant_id)));
        IF COL_LENGTH(N'dj_jobs',N'trace_parent') IS NULL
            ALTER TABLE dj_jobs ADD trace_parent nvarchar(64) NULL;
        IF COL_LENGTH(N'dj_flows',N'max_parallel') IS NULL
            ALTER TABLE dj_flows ADD max_parallel int NULL CONSTRAINT ck_dj_flows_max_parallel CHECK(max_parallel>0);
        IF COL_LENGTH(N'dj_jobs',N'original_payload') IS NULL
            ALTER TABLE dj_jobs ADD original_payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL;
        IF COL_LENGTH(N'dj_flows',N'parent_run_id') IS NULL
            ALTER TABLE dj_flows ADD parent_run_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NULL,
                parent_job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NULL, depth int NULL;
        IF COL_LENGTH(N'dj_queue_controls',N'weight') IS NULL
            ALTER TABLE dj_queue_controls ADD weight int NOT NULL CONSTRAINT df_dj_queue_controls_weight DEFAULT 1;
        IF OBJECT_ID(N'dj_queue_turns',N'U') IS NULL
            CREATE TABLE dj_queue_turns (
                queue nvarchar(512) COLLATE Latin1_General_100_BIN2 PRIMARY KEY NONCLUSTERED,
                pass bigint NOT NULL,
                CHECK(DATALENGTH(queue)=DATALENGTH(RTRIM(queue)))
            );
        IF OBJECT_ID(N'dj_queue_clock',N'U') IS NULL
            CREATE TABLE dj_queue_clock (id int PRIMARY KEY, value bigint NOT NULL);
        IF OBJECT_ID(N'dj_keyed_limits',N'U') IS NULL
            CREATE TABLE dj_keyed_limits (
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                key_scope int NOT NULL,
                maximum int NOT NULL CHECK(maximum>=0),
                PRIMARY KEY(job_key,key_scope),
                CHECK(DATALENGTH(job_key)=DATALENGTH(RTRIM(job_key)))
            );
        IF OBJECT_ID(N'dj_rate_ledger',N'U') IS NULL
            CREATE TABLE dj_rate_ledger (
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                key_scope int NOT NULL,
                rate_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                started_at bigint NOT NULL,
                job_id nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL
            );
        IF OBJECT_ID(N'dj_rate_limits',N'U') IS NULL
            CREATE TABLE dj_rate_limits (
                job_key nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
                key_scope int NOT NULL,
                permits int NOT NULL CHECK(permits>0),
                window_ms bigint NOT NULL CHECK(window_ms>0),
                PRIMARY KEY(job_key,key_scope),
                CHECK(DATALENGTH(job_key)=DATALENGTH(RTRIM(job_key)))
            );
        """;

    // A separate batch: SQL Server compiles a batch before its ALTER TABLE adds the indexed columns.
    private const string SqlServerAdmissionIndexes = """
        IF NOT EXISTS(SELECT 1 FROM dj_queue_clock WHERE id=1) INSERT INTO dj_queue_clock(id,value) VALUES (1,0);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_partition' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_partition ON dj_jobs(partition_key,status,lease_expires_at) WHERE partition_key IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_jobs_tenant' AND object_id=OBJECT_ID(N'dj_jobs')) CREATE INDEX ix_dj_jobs_tenant ON dj_jobs(tenant_id,status,lease_expires_at) WHERE tenant_id IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_flows_bounded' AND object_id=OBJECT_ID(N'dj_flows')) CREATE INDEX ix_dj_flows_bounded ON dj_flows(status) WHERE max_parallel IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_flows_parent' AND object_id=OBJECT_ID(N'dj_flows')) CREATE INDEX ix_dj_flows_parent ON dj_flows(parent_run_id) WHERE parent_run_id IS NOT NULL;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_rate_ledger' AND object_id=OBJECT_ID(N'dj_rate_ledger')) CREATE INDEX ix_dj_rate_ledger ON dj_rate_ledger(job_key,key_scope,rate_key,started_at);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'ix_dj_rate_ledger_time' AND object_id=OBJECT_ID(N'dj_rate_ledger')) CREATE INDEX ix_dj_rate_ledger_time ON dj_rate_ledger(started_at);
        """;
}
