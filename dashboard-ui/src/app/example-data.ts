// Fictional design fixtures. Never import these into a production data adapter.
export type StepState = 'Succeeded' | 'Failed' | 'Blocked';
export interface ExampleStep {
    readonly id: string;
    readonly name: string;
    readonly processor: string;
    readonly implementation: string;
    readonly assembly: string;
    readonly duration: string;
    readonly input: Readonly<Record<string, unknown>>;
    readonly output: Readonly<Record<string, unknown>>;
}

export const steps: readonly ExampleStep[] = [
    {
        id: 'validate',
        name: 'Validate order',
        processor: 'orders.validate',
        implementation: 'Commerce.Validation.OrderValidator',
        assembly: 'Commerce.Validation',
        duration: '83 ms',
        input: { orderId: '1042', amount: 249 },
        output: { approved: true },
    },
    {
        id: 'charge',
        name: 'Charge payment',
        processor: 'payments.charge',
        implementation: 'Billing.Processors.ChargePayment',
        assembly: 'Billing.Workers',
        duration: '30.0 s',
        input: { orderId: '1042', amount: 249, currency: 'EUR', paymentToken: '[redacted]' },
        output: { paymentId: 'pay_demo_1042', accepted: true },
    },
    {
        id: 'invoice',
        name: 'Create invoice',
        processor: 'invoices.create',
        implementation: 'Documents.InvoiceProcessor',
        assembly: 'Documents.Workers',
        duration: '412 ms',
        input: { orderId: '1042' },
        output: { documentId: 'inv_demo_1042' },
    },
    {
        id: 'notify',
        name: 'Send confirmation',
        processor: 'notifications.order',
        implementation: 'Messaging.OrderConfirmation',
        assembly: 'Messaging.Workers',
        duration: '126 ms',
        input: { orderId: '1042' },
        output: { delivered: true },
    },
];

export const completionBuckets = [92, 104, 89, 117, 108, 125, 121, 132, 118, 137, 145, 152];
export const recentJobs = [
    {
        id: 'job_8d21',
        processor: 'payments.charge',
        correlation: 'order:1042',
        status: 'Failed',
        attempt: '3 / 3',
        duration: '30.0 s',
        queue: 'billing',
        reason: 'Gateway timeout; outcome requires reconciliation.',
    },
    {
        id: 'job_8d22',
        processor: 'invoices.create',
        correlation: 'order:1042',
        status: 'Blocked',
        attempt: '0 / 3',
        duration: '—',
        queue: 'documents',
        reason: 'Predecessor payments.charge has an unresolved incident.',
    },
    {
        id: 'job_8d23',
        processor: 'notifications.order',
        correlation: 'order:1042',
        status: 'Blocked',
        attempt: '0 / 3',
        duration: '—',
        queue: 'default',
        reason: 'Waiting for invoice creation.',
    },
    {
        id: 'job_8c19',
        processor: 'exports.generate',
        correlation: 'report:oct-07',
        status: 'Running',
        attempt: '1 / 3',
        duration: '12.8 s elapsed',
        queue: 'exports',
        reason: 'Worker exports-02 reports 64% complete.',
    },
    {
        id: 'job_8c18',
        processor: 'orders.validate',
        correlation: 'order:1042',
        status: 'Succeeded',
        attempt: '1 / 3',
        duration: '83 ms',
        queue: 'default',
        reason: 'Approved; persisted output activated the charge step.',
    },
    {
        id: 'job_8c17',
        processor: 'invoices.create',
        correlation: 'order:1041',
        status: 'Succeeded',
        attempt: '1 / 3',
        duration: '412 ms',
        queue: 'documents',
        reason: 'Invoice output persisted successfully.',
    },
] as const;
