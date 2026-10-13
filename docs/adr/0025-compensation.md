# ADR 025: compensation

Status: accepted, implemented in engine protocol 5.

## Context

A flow that charges a card and reserves stock cannot roll those effects back in a database transaction; they live in other systems. When a later step fails for good, the business usually wants the earlier effects undone, which is the saga pattern. Until now the options were to stop (leaving effects in place) or to raise an incident for an operator.

## Decision

- A step may declare a compensating job: `CompensateWith(processor)`. The job receives `{"input": ..., "output": ...}`, which is `CompensationInput<TInput, TOutput>`: what the step was asked to do and what it reported.
- A new failure policy, `Compensate`, behaves like `Stop` and then undoes steps:
  - remaining waiting and queued steps are skipped;
  - every succeeded step with a compensation gets an undo step named `stepId:undo`.
- **Order:** an undo step runs once every step that depended on its step has finished and been undone. This reverses the dependency order, so parallel branches undo independently.
- **Completion:** the flow stays Running while undo steps run, with the original failure as its error. It becomes Failed when nothing remains to run, so steps still running at the time of the failure are awaited and undone too.
- **Failed undo:** a failed undo step raises an incident. Operators recover it like any step, and compensation continues.
- **Scope:** compensation applies to steps that run a job. Signal waits, child steps and fan-out steps cannot declare one in this version; a child flow can compensate its own steps.
- **Rollout:** older workers would not create or order undo steps, so these definitions need engine protocol 5 (ADR-021). No schema change is needed.

## Consequences

- Undo jobs are ordinary jobs, delivered at least once, so they must be idempotent like any handler; `context.IdempotencyKey` is stable for each undo step.
- Compensation is best effort in business terms: it runs code that the application provides. The engine guarantees the order and that each completed step is offered once for undoing.
- Planning a failure now keeps only the last change to each step within one pass. Previously, under Stop, a step queued and skipped in the same pass stayed queued in the failed flow.
