import { Component, ElementRef, computed, signal, viewChild } from '@angular/core';
import { DatePipe, DecimalPipe, JsonPipe } from '@angular/common';
import { SavedFlowGraph } from './saved-flow-graph';
import { ChildFlowProcessorKey, CsrfHeader, Permission, SignalProcessorKey } from './permissions';

interface Processor {
    key: string;
    version: number;
}
interface Job {
    id: string;
    contract: Processor;
    queue: string;
    correlationId: string | null;
    status: string;
    attempt: number;
    maxAttempts: number;
    createdAt: string;
    availableAt: string;
    workflowRunId: string | null;
    stepId: string | null;
    workerId: string | null;
    partitionKey: string | null;
    tenantId: string | null;
}
interface Attempt {
    number: number;
    status: string;
    startedAt: string;
    finishedAt: string | null;
    handler: string | null;
    error: string | null;
}
interface Detail {
    job: Job;
    health: { reason: string; suspectedStalled: boolean; progress: unknown };
    sensitive: boolean;
    payload: unknown;
    originalPayload: unknown;
    output: unknown;
    error: string | null;
    attempts: Attempt[];
    failures:
        | {
              attempt: number;
              failure: { exceptionType: string; message: string; stackTrace: string | null };
          }[]
        | null;
}
interface Graph {
    key: string;
    version: number;
    steps: {
        id: string;
        displayName?: string | null;
        contract: Processor;
        after: string[];
        queue: string;
        conditional: boolean;
        onFailureOf?: string | null;
    }[];
}
interface Definition {
    key: string;
    version: number;
    revision: number;
    status: string;
    graph: Graph;
}
interface FlowRun {
    id: string;
    flowKey: string;
    version: number;
    status: string;
    parentRunId?: string | null;
    parentJobId?: string | null;
    forkedFromRunId?: string | null;
}
interface Flow {
    jobs: Job[];
    flow: FlowRun;
    children: { run: FlowRun; jobId: string | null }[];
    graph: Graph;
    events: { sequence: number; jobId: string; kind: string; at: string; attempt: number }[];
}
interface Schedule {
    key: string;
    version: number;
    revision: number;
    cron: string;
    timeZone: string;
    processor: Processor;
    status: string;
    enabled: boolean;
    nextOccurrence: string | null;
    overlap: string;
    misfire: string;
}
interface EngineProtocolState {
    active: number;
    supported: number;
    blockers: string[];
}
interface Worker {
    acquisition?: {
        lastAttemptAt: string | null;
        lastSucceededAt: string | null;
        consecutiveFailures: number;
    } | null;
    descriptions?: {
        identity: Processor;
        canonicalIdentity: Processor;
        displayName: string | null;
        upcastTo?: Processor | null;
    }[];
    protocol?: number;
    workerId: string;
    seenAt: string;
    processors: Processor[];
    queues: string[];
    capacity: number;
    running: number;
}
interface Audit {
    commandId: string;
    at: string;
    actor: { displayName: string | null; subject: string };
    action: string;
    target: string;
    reason: string;
    outcome: string;
}
interface Statistics {
    counts: Record<string, number>;
    limits: { jobKey: string | null; maximum: number }[];
}
interface RuntimeMetrics {
    from: string;
    until: string;
    succeededAttempts: number;
    failedAttempts: number;
    meanDurationMilliseconds: number | null;
    maximumDurationMilliseconds: number | null;
    successfulAttemptsPerSecond: number;
}
interface DeploymentReport {
    observedAt: string;
    isTruncated: boolean;
    allRequirementsCovered: boolean;
    checks: {
        requirement: {
            processor: Processor;
            queue: string;
            outstandingJobs: number;
            readyJobs: number;
            oldestReadyAt: string | null;
            enabledSchedules: number;
        };
        coverage: string;
        workers: string[];
    }[];
}
interface QueueControl {
    queue: string;
    paused: boolean;
    revision: number;
    weight: number;
}
type KeyScope = 'Partition' | 'Tenant';
const DurationUnits: [number, string][] = [
    [86400, 'd'],
    [3600, 'h'],
    [60, 'min'],
    [1, 's'],
];
interface AdmissionPolicies {
    keyedLimits: { scope: KeyScope; jobKey: string | null; maximumPerKey: number }[];
    rateLimits: {
        jobKey: string | null;
        permits: number;
        window: string;
        perKey: KeyScope | null;
    }[];
}
interface WorkerControl {
    workerId: string;
    draining: boolean;
    revision: number;
}
interface Controls {
    queues: QueueControl[];
    workers: WorkerControl[];
}
interface Archive {
    id: string;
    kind: string;
    correlationId: string | null;
    archivedAt: string;
    jobs: number;
    sha256: string;
    bodyAvailable: boolean;
}
interface Candidate {
    id: string;
    kind: string;
    correlationId: string | null;
    completedAt: string;
    jobs: number;
}
interface Pending {
    title: string;
    path: string;
    expectedAttempt?: number;
    expectedRevision?: number;
    wrapper?: 'command';
    value?: object;
    command?: object;
    /** Initial JSON for an input the operator edits in the dialog. */
    input?: string;
}

interface RetentionStatus {
    lastFailureCode: string | null;
    lastFailureAt: string | null;
    configuration: {
        revision: number;
        policy: {
            enabled: boolean;
            succeededAfter: string;
            failedAfter: string | null;
            purgeAfter: string | null;
            exportDestination: string | null;
            eventsAfter: string | null;
            batchSize: number;
        };
    } | null;
    pendingBatchId: string | null;
    maintenanceActive: boolean;
    leaseExpiresAt: string | null;
    lastBatch: {
        batchId: string;
        outcome: string;
        completedAt: string;
        archived: string[];
        purged: string[];
        blocked: string[];
        eventsTrimmed: number;
    } | null;
    heldUnits: number;
    incidentCount: number;
    incidentExamples: { unitId: string; code: string; at: string }[];
    retainedAfterEvent: number;
    lastEventSequence: number;
    slowestSubscriberPosition: number | null;
}

const DayMilliseconds = 86_400_000;
const DefaultArchiveAgeDays = 30;

// failurePolicy 1 is FlowFailurePolicy.Suspend: dependent work is kept for recovery.
const EmptyFlowDraft = JSON.stringify(
    { key: 'my.flow', version: 1, steps: [], failurePolicy: 1 },
    null,
    2,
);

const AppearanceKey = 'workthrough.appearance';

/** The viewer's saved appearance, else their system preference. Storage can be unavailable, as in private windows. */
function initialAppearance(): 'light' | 'dark' {
    try {
        const saved = localStorage.getItem(AppearanceKey);
        if (saved === 'light' || saved === 'dark') return saved;
    } catch {}
    return matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

@Component({
    selector: 'app-console',
    imports: [DatePipe, DecimalPipe, JsonPipe, SavedFlowGraph],
    templateUrl: './live-console.html',
})
export class LiveConsoleComponent {
    readonly permission = Permission;
    readonly signalProcessorKey = SignalProcessorKey;
    readonly childFlowProcessorKey = ChildFlowProcessorKey;
    readonly base = document.body.dataset['base'] ?? '';
    readonly light = signal(initialAppearance() === 'light');
    readonly section = signal('executions');
    readonly busy = signal(false);
    readonly error = signal('');
    readonly updated = signal<Date | null>(null);
    readonly permissions = signal<string[]>([]);
    readonly user = signal('');
    /** Null for store-wide access; otherwise the only tenants this user can see (ADR-019). */
    readonly tenants = signal<string[] | null>(null);
    readonly storeWide = computed(() => this.tenants() === null);
    readonly jobs = signal<Job[]>([]);
    readonly stats = signal<Statistics>({ counts: {}, limits: [] });
    readonly metrics = signal<RuntimeMetrics | null>(null);
    readonly metricsError = signal('');
    readonly definitions = signal<Definition[]>([]);
    readonly workers = signal<Worker[]>([]);
    readonly protocol = signal<EngineProtocolState | null>(null);
    readonly schedules = signal<Schedule[]>([]);
    readonly deployment = signal<DeploymentReport | null>(null);
    readonly controls = signal<Controls>({ queues: [], workers: [] });
    readonly archives = signal<Archive[]>([]);
    readonly admission = signal<AdmissionPolicies>({ keyedLimits: [], rateLimits: [] });
    readonly archiveCandidates = signal<Candidate[] | null>(null);
    readonly retention = signal<RetentionStatus | null>(null);
    readonly retentionError = signal('');
    archiveBefore = new Date(Date.now() - DefaultArchiveAgeDays * DayMilliseconds)
        .toISOString()
        .slice(0, 16);
    readonly audit = signal<Audit[]>([]);
    readonly detail = signal<Detail | null>(null);
    readonly flow = signal<Flow | null>(null);
    readonly pending = signal<Pending | null>(null);
    readonly inputJson = signal('');
    readonly confirmation = viewChild<ElementRef<HTMLDialogElement>>('confirmation');
    readonly draftEditor = signal(false);
    readonly draftJson = signal(EmptyFlowDraft);
    readonly draftRevision = signal(0);
    readonly commandError = signal('');
    readonly reason = signal('');
    readonly submitting = signal(false);
    correlation = '';
    status = '';
    offset = 0;
    private inspection = 0;
    private draftKind = 'flows';

    constructor() {
        void this.initialize();
    }
    can(permission: Permission) {
        return this.permissions().includes(permission);
    }
    async request<T>(path: string, body?: object): Promise<T> {
        const headers: Record<string, string> = {};
        if (body) {
            const csrf = await this.request<{ token: string }>('/api/csrf');
            headers[CsrfHeader] = csrf.token;
            headers['Content-Type'] = 'application/json';
        }
        const response = await fetch(this.base + path, {
            method: body ? 'POST' : 'GET',
            cache: 'no-store',
            credentials: 'same-origin',
            headers,
            body: body ? JSON.stringify(body) : undefined,
        });
        if (!response.ok) {
            const problem = await response.json().catch(() => ({}));
            throw new Error(
                problem.error ??
                    (response.status === 403
                        ? 'Your account does not have permission for this action.'
                        : response.status === 401
                          ? 'Sign in through the dashboard host to continue.'
                          : `Request failed (${response.status}).`),
            );
        }
        return response.json() as Promise<T>;
    }
    async initialize() {
        try {
            const access = await this.request<{
                permissions: string[];
                user: string;
                tenants: string[] | null;
            }>('/api/access');
            this.permissions.set(access.permissions);
            this.user.set(access.user);
            this.tenants.set(access.tenants ?? null);
            if (
                !this.can(Permission.ReadExecutions) &&
                this.storeWide() &&
                this.can(Permission.ReadAudit)
            )
                this.section.set('audit');
            await this.refresh();
        } catch (e) {
            this.error.set(this.message(e));
        }
    }
    async refresh() {
        if (this.busy()) return;
        this.busy.set(true);
        this.error.set('');
        try {
            if (this.can(Permission.ReadExecutions) && !this.storeWide()) {
                const filter = new URLSearchParams({ offset: String(this.offset) });
                if (this.correlation) filter.set('correlationId', this.correlation);
                if (this.status) filter.set('status', this.status);
                this.jobs.set(await this.request<Job[]>('/api/jobs?' + filter));
            } else if (this.can(Permission.ReadExecutions)) {
                const filter = new URLSearchParams({ offset: String(this.offset) });
                if (this.correlation) filter.set('correlationId', this.correlation);
                if (this.status) filter.set('status', this.status);
                const [
                    jobs,
                    stats,
                    definitions,
                    workers,
                    schedules,
                    deployment,
                    controls,
                    archives,
                    admission,
                ] = await Promise.all([
                    this.request<Job[]>('/api/jobs?' + filter),
                    this.request<Statistics>('/api/stats'),
                    this.request<Definition[]>('/api/definitions'),
                    this.request<Worker[]>('/api/workers'),
                    this.request<Schedule[]>('/api/schedules'),
                    this.request<DeploymentReport>('/api/deployment'),
                    this.request<Controls>('/api/controls'),
                    this.request<Archive[]>('/api/archives?' + filter),
                    this.request<AdmissionPolicies>('/api/admission'),
                ]);
                this.controls.set(controls);
                this.archives.set(archives);
                this.admission.set(admission);
                this.jobs.set(jobs);
                this.stats.set(stats);
                this.definitions.set(definitions);
                this.workers.set(workers);
                this.schedules.set(schedules);
                this.deployment.set(deployment);
                try {
                    this.protocol.set(await this.request<EngineProtocolState>('/api/protocol'));
                } catch {
                    this.protocol.set(null);
                }
                try {
                    this.metrics.set(await this.request<RuntimeMetrics>('/api/metrics'));
                    this.metricsError.set('');
                } catch {
                    this.metrics.set(null);
                    this.metricsError.set(
                        'Windowed metrics are unavailable. Stored job totals remain available.',
                    );
                }
            }
            if (this.storeWide() && this.can(Permission.ReadAudit))
                this.audit.set(await this.request<Audit[]>('/api/audit'));
            if (
                this.storeWide() &&
                this.can(Permission.ReadExecutions) &&
                this.can(Permission.ReadRetention)
            ) {
                try {
                    this.retention.set(await this.request<RetentionStatus>('/api/retention'));
                    this.retentionError.set('');
                } catch {
                    this.retention.set(null);
                    this.retentionError.set('Automatic retention status is unavailable.');
                }
            }
            this.updated.set(new Date());
            const selected = this.detail();
            if (selected) await this.inspect(selected.job);
        } catch (e) {
            this.error.set(this.message(e));
        } finally {
            this.busy.set(false);
        }
    }
    navigate(section: string) {
        ++this.inspection;
        this.section.set(section);
        this.detail.set(null);
        this.flow.set(null);
        this.draftEditor.set(false);
    }
    async inspect(job: Job) {
        const version = ++this.inspection;
        this.error.set('');
        this.flow.set(null);
        try {
            const detail = await this.request<Detail>('/api/jobs/' + job.id);
            if (version !== this.inspection) return;
            this.detail.set(detail);
            if (job.workflowRunId) {
                const flow = await this.request<Flow>('/api/flows/' + job.workflowRunId);
                if (version === this.inspection) this.flow.set(flow);
            }
        } catch (e) {
            if (version === this.inspection) this.error.set(this.message(e));
        }
    }
    /** Opens a job by ID, for links between parent and child runs. */
    async inspectRun(jobId: string | null | undefined, runId: string | null | undefined) {
        if (jobId && runId) await this.inspect({ id: jobId, workflowRunId: runId } as Job);
    }
    async inspectStep(id: string) {
        const job = this.flow()?.jobs.find(
            (j) => j.workflowRunId === this.flow()?.flow.id && j.stepId === id,
        );
        if (job) await this.inspect(job);
    }
    stepStatus(id: string) {
        return this.flow()?.jobs.find((j) => j.stepId === id)?.status ?? 'Unknown';
    }
    acquisitionStatus(worker: Worker) {
        if (
            this.updated() &&
            this.updated()!.getTime() - new Date(worker.seenAt).getTime() > 60_000
        )
            return 'Heartbeat stale';
        if (worker.acquisition == null) return 'Not reported';
        if (worker.acquisition.consecutiveFailures > 0)
            return `${worker.acquisition.consecutiveFailures} consecutive polling failures`;
        return worker.acquisition.lastSucceededAt ? 'Last poll succeeded' : 'No poll recorded';
    }
    applyFilters() {
        this.offset = 0;
        void this.refresh();
    }
    page(direction: number) {
        this.offset = Math.max(0, this.offset + direction * 100);
        void this.refresh();
    }
    /** Starts a new run of this flow from the step, keeping the outcomes of earlier work. */
    openFork(flow: Flow, stepId: string) {
        this.ask({
            title: `Fork a new run from step ${stepId}`,
            path: `/api/flows/${flow.flow.id}/fork`,
            wrapper: 'command',
            value: { fromStep: stepId },
        });
    }
    /** Opens the first job of another run, such as a fork's source. */
    async inspectFirstJob(runId: string | null | undefined) {
        if (!runId) return;
        const source = await this.request<Flow>('/api/flows/' + runId);
        await this.inspectRun(source.jobs[0]?.id, runId);
    }
    /** Edits a failed job's input, then retries it, or recovers it when it is a step of a flow in an incident. */
    openCorrection(job: Job, payload: unknown) {
        this.ask({
            title: 'Correct input and run again',
            path: `/api/jobs/${job.id}/correct`,
            expectedAttempt: job.attempt,
            wrapper: 'command',
            input: JSON.stringify(payload, null, 2),
        });
    }
    openAction(action: string, job: Job) {
        this.ask({
            title:
                action === 'replay'
                    ? 'Replay as a new job with a new effect key'
                    : action === 'recover'
                      ? 'Recover failed step'
                      : action === 'retry'
                        ? 'Retry job'
                        : 'Cancel job',
            path: `/api/jobs/${job.id}/${action}`,
            expectedAttempt: job.attempt,
        });
    }
    queues(): QueueControl[] {
        const names = new Set([
            ...this.controls().queues.map((q) => q.queue),
            ...this.workers().flatMap((w) => w.queues),
            ...this.jobs().map((j) => j.queue),
        ]);
        return [...names].sort().map(
            (queue) =>
                this.controls().queues.find((q) => q.queue === queue) ?? {
                    queue,
                    paused: false,
                    revision: 0,
                    weight: 1,
                },
        );
    }
    /** Stored totals; terminal problems are grouped because an operator acts on them the same way. */
    readonly totals = computed(() => {
        const count = (...states: string[]) =>
            states.reduce((sum, state) => sum + (this.stats().counts[state] ?? 0), 0);
        return [
            { label: 'Queued', count: count('Queued'), detail: 'Ready for a worker' },
            { label: 'Running', count: count('Running'), detail: 'Holding a lease' },
            { label: 'Succeeded', count: count('Succeeded'), detail: 'Current stored jobs' },
            {
                label: 'Needs attention',
                count: count('Failed', 'Rejected', 'Expired'),
                detail: 'Failed, rejected or expired',
            },
        ];
    });
    /** Formats a whole .NET TimeSpan ("d.hh:mm:ss") in its largest exact unit, e.g. "1 min"; anything else stays as stored. */
    duration(value: string): string {
        const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)$/.exec(value);
        if (!match) return value;
        const [, days = '0', hours, minutes, seconds] = match;
        const total =
            ((Number(days) * 24 + Number(hours)) * 60 + Number(minutes)) * 60 + Number(seconds);
        const [size, label] = DurationUnits.find(([unit]) => total % unit === 0) ?? [1, 's'];
        return `${total / size} ${label}`;
    }
    keyScopeLabel(scope: KeyScope): string {
        return scope === 'Tenant' ? 'tenant' : 'partition key';
    }
    /** The version whose job runs this processor address through an upcast, if any. */
    upcastTarget(worker: Worker, processor: Processor): Processor | null {
        return (
            worker.descriptions?.find(
                (d) => d.identity.key === processor.key && d.identity.version === processor.version,
            )?.upcastTo ?? null
        );
    }
    processorName(worker: Worker, processor: Processor): string {
        return (
            worker.descriptions?.find(
                (d) => d.identity.key === processor.key && d.identity.version === processor.version,
            )?.displayName ?? processor.key
        );
    }
    workerControl(workerId: string): WorkerControl {
        return (
            this.controls().workers.find((w) => w.workerId === workerId) ?? {
                workerId,
                draining: false,
                revision: 0,
            }
        );
    }
    queueAction(queue: QueueControl) {
        this.ask({
            title: `${queue.paused ? 'Resume' : 'Pause'} queue ${queue.queue}`,
            path: '/api/queues',
            expectedRevision: queue.revision,
            wrapper: 'command',
            value: { queue: queue.queue, paused: !queue.paused },
        });
    }
    workerAction(workerId: string) {
        const control = this.workerControl(workerId);
        this.ask({
            title: `${control.draining ? 'Resume' : 'Drain'} worker ${workerId}`,
            path: '/api/workers',
            expectedRevision: control.revision,
            wrapper: 'command',
            value: { workerId, draining: !control.draining },
        });
    }
    async previewArchive() {
        try {
            const before = new Date(this.archiveBefore + 'Z').toISOString();
            this.archiveCandidates.set(
                await this.request<Candidate[]>(
                    '/api/archives/preview?' + new URLSearchParams({ succeededBefore: before }),
                ),
            );
        } catch (e) {
            this.error.set(this.message(e));
        }
    }
    archiveAction() {
        this.ask({
            title: 'Archive up to 10 completed executions or whole flows',
            path: '/api/archives',
            wrapper: 'command',
            value: {
                policy: {
                    succeededBefore: new Date(this.archiveBefore + 'Z').toISOString(),
                    batchSize: 10,
                },
            },
        });
    }
    definitionAction(action: string, definition: Definition) {
        this.ask({
            title: `${action === 'publish' ? 'Publish' : 'Retire'} ${definition.key} · v${definition.version}`,
            path: `/api/definitions/${encodeURIComponent(definition.key)}/${definition.version}/${action}`,
            expectedRevision: definition.revision,
        });
    }
    flowAction(action: string) {
        const flow = this.flow();
        if (flow)
            this.ask({
                title: `${action === 'pause' ? 'Pause' : 'Resume'} flow admission`,
                path: `/api/flows/${flow.flow.id}/${action}`,
            });
    }
    scheduleAction(action: string, schedule: Schedule) {
        this.ask({
            title: `${action} ${schedule.key} · v${schedule.version}`,
            path: `/api/schedules/${encodeURIComponent(schedule.key)}/${schedule.version}/${action}`,
            expectedRevision: schedule.revision,
        });
    }
    openScheduleEditor() {
        this.draftEditor.set(!this.draftEditor());
        if (this.draftKind === 'schedules') return;
        this.draftKind = 'schedules';
        this.draftRevision.set(0);
        this.draftJson.set(
            JSON.stringify(
                {
                    key: 'my.schedule',
                    version: 1,
                    cron: '0 * * * *',
                    timeZone: 'UTC',
                    processor: { key: 'my.processor', version: 1 },
                    input: '{}',
                    queue: 'default',
                    maxAttempts: 3,
                    overlap: 1,
                    misfire: 1,
                },
                null,
                2,
            ),
        );
    }
    openFlowEditor() {
        this.draftEditor.set(!this.draftEditor());
        if (this.draftKind === 'flows') return;
        this.draftKind = 'flows';
        this.draftRevision.set(0);
        this.draftJson.set(EmptyFlowDraft);
    }
    saveDraft() {
        try {
            this.ask({
                title: this.section() === 'schedules' ? 'Save schedule draft' : 'Save flow draft',
                path:
                    this.section() === 'schedules'
                        ? '/api/schedules/draft'
                        : '/api/definitions/draft',
                wrapper: 'command',
                value: { definition: JSON.parse(this.draftJson()) },
                expectedRevision: this.draftRevision(),
            });
        } catch {
            this.error.set('The draft must be valid JSON. Publishing will validate its graph.');
        }
    }
    ask(pending: Pending) {
        this.pending.set(pending);
        this.inputJson.set(pending.input ?? '');
        this.reason.set('');
        this.commandError.set('');
        this.confirmation()?.nativeElement.showModal();
    }
    closeConfirmation() {
        if (!this.submitting()) this.confirmation()?.nativeElement.close();
    }
    async confirm() {
        const pending = this.pending();
        if (!pending || !this.reason().trim() || this.submitting()) return;
        let value = pending.value;
        if (pending.input !== undefined) {
            try {
                value = { input: JSON.parse(this.inputJson()) };
            } catch {
                this.commandError.set('The input must be valid JSON.');
                return;
            }
        }
        this.submitting.set(true);
        this.commandError.set('');
        const command = pending.command ?? {
            commandId: crypto.randomUUID(),
            reason: this.reason(),
            expectedAttempt: pending.expectedAttempt ?? 0,
            expectedRevision: pending.expectedRevision ?? 0,
        };
        this.pending.set({ ...pending, command });
        try {
            const result = await this.request<unknown>(
                pending.path,
                pending.wrapper ? { ...value, command } : command,
            );
            if (pending.path.endsWith('/draft') && typeof result === 'number')
                this.draftRevision.set(result);
            this.confirmation()?.nativeElement.close();
            this.archiveCandidates.set(null);
            await this.refresh();
        } catch (e) {
            this.commandError.set(this.message(e) + ' Retrying here uses the same command ID.');
        } finally {
            this.submitting.set(false);
        }
    }
    label(reason: string) {
        return (
            (
                {
                    FlowIncident: 'Blocked by a flow incident',
                    FlowPaused: 'Flow admission paused',
                    AwaitingSignal: 'Waiting for an external signal',
                    QueuePaused: 'Queue admission paused',
                    WorkersDraining: 'Capable workers are draining',
                    Dependencies: 'Waiting for predecessor steps',
                    RetryScheduled: 'Waiting for the next retry',
                    Scheduled: 'Scheduled for later',
                    SharedCapacity: 'Waiting for shared capacity',
                    PartitionCapacity: 'Waiting for a free slot in its partition',
                    TenantCapacity: 'Waiting for a free slot for its tenant',
                    FlowParallelism: 'Waiting for its flow run to finish a parallel step',
                    RateLimited: 'Waiting for the rate limit window',
                    WorkerCapacity: 'Waiting for worker capacity',
                    NoRecentCapableWorker: 'No recent worker supports this processor and queue',
                    LeaseExpired: 'Lease expired · awaiting recovery',
                    ExecutionDeadlineExceeded: 'Suspected stalled · execution deadline exceeded',
                    ProgressOverdue: 'Suspected stalled · progress overdue',
                    MissingProcessor: 'Processor version missing',
                    MissingQueue: 'Matching processor does not consume this queue',
                    Covered: 'Covered',
                } as Record<string, string>
            )[reason] ?? reason
        );
    }
    toggleAppearance(): void {
        this.light.set(!this.light());
        try {
            localStorage.setItem(AppearanceKey, this.light() ? 'light' : 'dark');
        } catch {}
    }
    private message(e: unknown) {
        return e instanceof Error ? e.message : 'The request failed.';
    }
}
