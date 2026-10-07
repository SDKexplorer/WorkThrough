CREATE TABLE dj_schema (version INTEGER PRIMARY KEY);
INSERT INTO dj_schema VALUES (1);
CREATE TABLE dj_jobs (
 id TEXT PRIMARY KEY,job_key TEXT NOT NULL,contract_version INTEGER NOT NULL,payload TEXT NOT NULL,
 queue TEXT NOT NULL,correlation_id TEXT,status TEXT NOT NULL,attempt INTEGER NOT NULL,max_attempts INTEGER NOT NULL,
 created_at BIGINT NOT NULL,available_at BIGINT NOT NULL,idempotency_key TEXT UNIQUE,workflow_run_id TEXT,step_id TEXT,
 output TEXT,error TEXT,worker_id TEXT,lease_token TEXT,lease_expires_at BIGINT,UNIQUE(workflow_run_id,step_id));
INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,correlation_id,status,attempt,max_attempts,created_at,available_at)
 VALUES ('11111111-1111-1111-1111-111111111111','tests.alpha',1,'{"value":"schema-one"}','default','legacy-fixture','Queued',0,3,1700000000000,1700000000000);
CREATE TABLE dj_definitions (flow_key TEXT NOT NULL,version INTEGER NOT NULL,definition TEXT NOT NULL,PRIMARY KEY(flow_key,version));
INSERT INTO dj_definitions VALUES ('fixtures.flow',1,'{"key":"fixtures.flow","version":1,"steps":[{"id":"legacy","contract":{"key":"tests.alpha","version":1},"input":{"value":"legacy"},"queue":"default","after":[],"bindings":[],"condition":null,"maxAttempts":1}]}');
CREATE TABLE dj_flows (id TEXT PRIMARY KEY,flow_key TEXT NOT NULL,version INTEGER NOT NULL,input TEXT NOT NULL,correlation_id TEXT,status TEXT NOT NULL,error TEXT);
