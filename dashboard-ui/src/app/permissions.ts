/** Permission names granted by the dashboard host. Mirrors OperationPermissions in WorkThrough.Core. */
export const Permission = {
    ReadExecutions: 'executions.read',
    ReadPayloads: 'payloads.read',
    ReadAudit: 'audit.read',
    ReadRetention: 'retention.read',
    RetryJobs: 'jobs.retry',
    CancelJobs: 'jobs.cancel',
    ReplayJobs: 'jobs.replay',
    RecoverFlows: 'flows.recover',
    EditDefinitions: 'definitions.edit',
    PublishDefinitions: 'definitions.publish',
    RetireDefinitions: 'definitions.retire',
    ManageSchedules: 'schedules.manage',
    ManageQueues: 'queues.manage',
    ManageWorkers: 'workers.manage',
    ManageArchives: 'archives.manage',
} as const;

export type Permission = (typeof Permission)[keyof typeof Permission];

/** Reserved engine address of durable signal waits. They are never replayed as processor work. */
export const SignalProcessorKey = 'workthrough.signal';

/** Must match the anti-forgery header configured by the dashboard host. */
export const CsrfHeader = 'X-CSRF-TOKEN';
