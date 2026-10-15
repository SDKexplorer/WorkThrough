# ADR 028: failure branches

Status: accepted, implemented in engine protocol 8.

## Context

A flow's failure policy applies to the whole run: suspend for an operator, stop, or compensate. Many processes instead have an expected alternative path for one step: tell the customer when a charge is declined, send a reminder when an approval times out, fall back to another carrier. Modelling these as incidents makes routine outcomes look like outages.

## Decision

A step can be a failure branch of one predecessor.

- `FlowBuilder.OnFailureOf("charge")` makes the last step depend only on `charge` and records `OnFailureOf` on the saved step.
- The branch runs when that step ends unsuccessfully: failed after its retries, rejected, cancelled, or expired. An expired signal wait or a failed child flow counts, so timeouts and child failures can be handled too. When the step succeeds or is skipped, the branch is skipped.
- The failure is handled: the flow's failure policy does not apply to it, and the run continues. The failed step's ordinary successors are skipped as usual. A later step that depends on both paths runs when either path succeeds, so branches can rejoin.
- A run whose only failures are handled succeeds. The failed step keeps its status and error in history.
- The branch's input is its bound input plus `failure`: `{"step": "charge", "error": "…"}`.
- If the branch itself fails, that failure applies the policy, unless another branch handles it.
- A branch needs an object input and no condition. Fan-out steps cannot have a branch yet, because their items fail individually.
- An older worker would treat the handled failure as unhandled. Definitions with branches therefore need engine protocol 8 (ADR-021).

## Consequences

- Conditions stay simple: success or failure of one step, and equality checks on outputs. A general expression language remains out of scope.
- A handled failure cannot be recovered as an incident; it already took its alternative path. A checkpoint fork can rerun the run from the failed step.
- The console marks branches as "If <step> fails" with a dashed edge.
