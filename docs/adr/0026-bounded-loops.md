# ADR 026: bounded loops

Status: accepted, implemented in engine protocol 6.

## Context

Flows are acyclic graphs (ADR-002). That keeps them inspectable and their plans finite. But some processes repeat: poll a carrier until a parcel is delivered, fetch pages until the last one, retry an approval round. Jobs can already defer themselves to poll, and fan-out handles known collections. What remained missing was repeating a group of steps an unknown number of times.

## Decision

A loop repeats a child flow (ADR-023) rather than adding cycles to the graph.

- `FlowBuilder.Repeat(id, childDefinition, untilStep, untilProperty, equals, maximumIterations, input)` declares a child step with a `FlowLoop`.
- Each iteration is a separate child run. Its input is the step's bound input plus `iteration` (1, 2, …) and `previous`, the last iteration's output. A child can therefore page or poll by reading them.
- When an iteration succeeds, the engine compares the named property of the named final step's output with the expected value. If they match, the step succeeds with that iteration's output. If not, and iterations remain, it starts the next iteration in the same transaction and keeps waiting. When none remain, the step fails.
- An iteration that fails fails the step, following the parent's failure policy. Recovering the step starts its iterations again; the iteration count continues from the child runs it already has.
- Loops are bounded at 1,000 iterations, and every iteration is an ordinary run, listed as a child of the parent and visible in the console.
- Older workers would treat a loop as a single child run, so loops need engine protocol 6 (ADR-021).

## Consequences

- Graphs stay acyclic; repetition lives in data (child runs), so history shows every iteration.
- Conditions are equality on one output property, like step conditions. Richer expressions remain out of scope.
- Each iteration has the child step's timeout; the loop as a whole has no separate deadline.
