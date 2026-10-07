import { Component, ElementRef, signal, viewChild } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { SavedFlowGraph } from './saved-flow-graph';

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
        contract: Processor;
        after: string[];
        queue: string;
        conditional: boolean;
    }[];
}
interface Definition {
    key: string;
    version: number;
    revision: number;
    status: string;
    graph: Graph;
}
interface Flow {
    jobs: Job[];
    flow: { id: string; flowKey: string; version: number; status: string };
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
interface Worker {
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
interface Pending {
    title: string;
    path: string;
    expectedAttempt?: number;
    expectedRevision?: number;
    wrapper?: 'command';
    value?: object;
    command?: object;
}

@Component({
    selector: 'app-console',
    imports: [DatePipe, JsonPipe, SavedFlowGraph],
    templateUrl: './live-console.html',
})
export class LiveConsoleComponent {
    readonly base = document.body.dataset['base'] ?? '';
    readonly light = signal(false);
    readonly section = signal('executions');
    readonly busy = signal(false);
    readonly error = signal('');
    readonly updated = signal<Date | null>(null);
    readonly permissions = signal<string[]>([]);
    readonly user = signal('');
    readonly jobs = signal<Job[]>([]);
    readonly stats = signal<Statistics>({ counts: {}, limits: [] });
    readonly definitions = signal<Definition[]>([]);
    readonly workers = signal<Worker[]>([]);
    readonly schedules = signal<Schedule[]>([]);
    readonly audit = signal<Audit[]>([]);
    readonly detail = signal<Detail | null>(null);
    readonly flow = signal<Flow | null>(null);
    readonly pending = signal<Pending | null>(null);
    readonly confirmation = viewChild<ElementRef<HTMLDialogElement>>('confirmation');
    readonly draftEditor = signal(false);
    readonly draftJson = signal(
        JSON.stringify({ key: 'my.flow', version: 1, steps: [], failurePolicy: 1 }, null, 2),
    );
    readonly draftRevision = signal(0);
    readonly commandError = signal('');
    readonly reason = signal('');
    readonly submitting = signal(false);
    correlation = '';
    status = '';
    offset = 0;
    private inspection = 0;

    constructor() {
        void this.initialize();
    }
    can(permission: string) {
        return this.permissions().includes(permission);
    }
    async request<T>(path: string, body?: object): Promise<T> {
        const headers: Record<string, string> = {};
        if (body) {
            const csrf = await this.request<{ token: string }>('/api/csrf');
            headers['X-CSRF-TOKEN'] = csrf.token;
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
            const access = await this.request<{ permissions: string[]; user: string }>(
                '/api/access',
            );
            this.permissions.set(access.permissions);
            this.user.set(access.user);
            if (!this.can('executions.read') && this.can('audit.read')) this.section.set('audit');
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
            if (this.can('executions.read')) {
                const filter = new URLSearchParams({ offset: String(this.offset) });
                if (this.correlation) filter.set('correlationId', this.correlation);
                if (this.status) filter.set('status', this.status);
                const [jobs, stats, definitions, workers, schedules] = await Promise.all([
                    this.request<Job[]>('/api/jobs?' + filter),
                    this.request<Statistics>('/api/stats'),
                    this.request<Definition[]>('/api/definitions'),
                    this.request<Worker[]>('/api/workers'),
                    this.request<Schedule[]>('/api/schedules'),
                ]);
                this.jobs.set(jobs);
                this.stats.set(stats);
                this.definitions.set(definitions);
                this.workers.set(workers);
                this.schedules.set(schedules);
            }
            if (this.can('audit.read')) this.audit.set(await this.request<Audit[]>('/api/audit'));
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
        this.section.set(section);
        this.detail.set(null);
        this.flow.set(null);
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
    async inspectStep(id: string) {
        const job = this.flow()?.jobs.find(
            (j) => j.workflowRunId === this.flow()?.flow.id && j.stepId === id,
        );
        if (job) await this.inspect(job);
    }
    stepStatus(id: string) {
        return this.flow()?.jobs.find((j) => j.stepId === id)?.status ?? 'Unknown';
    }
    applyFilters() {
        this.offset = 0;
        void this.refresh();
    }
    page(direction: number) {
        this.offset = Math.max(0, this.offset + direction * 100);
        void this.refresh();
    }
    openAction(action: string, job: Job) {
        this.ask({
            title:
                action === 'recover'
                    ? 'Recover failed step'
                    : action === 'retry'
                      ? 'Retry job'
                      : 'Cancel job',
            path: `/api/jobs/${job.id}/${action}`,
            expectedAttempt: job.attempt,
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
                pending.wrapper ? { ...pending.value, command } : command,
            );
            if (pending.path.endsWith('/draft') && typeof result === 'number')
                this.draftRevision.set(result);
            this.confirmation()?.nativeElement.close();
            await this.refresh();
            const detail = this.detail();
            if (detail) await this.inspect(detail.job);
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
                    Dependencies: 'Waiting for predecessor steps',
                    RetryScheduled: 'Waiting for the next retry',
                    Scheduled: 'Scheduled for later',
                    SharedCapacity: 'Waiting for shared capacity',
                    WorkerCapacity: 'Waiting for worker capacity',
                    NoRecentCapableWorker: 'No recent worker supports this processor and queue',
                    LeaseExpired: 'Lease expired · awaiting recovery',
                    ExecutionDeadlineExceeded: 'Suspected stalled · execution deadline exceeded',
                    ProgressOverdue: 'Suspected stalled · progress overdue',
                } as Record<string, string>
            )[reason] ?? reason
        );
    }
    private message(e: unknown) {
        return e instanceof Error ? e.message : 'The request failed.';
    }
}
