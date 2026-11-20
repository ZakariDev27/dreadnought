# 2. The gate lives outside the control plane

## Context
Deciding whether a ring is healthy is statistics over telemetry. Deciding what a rollout may do
next is a state machine with authorisation. They change for different reasons and at different speeds.

## Decision
The control plane (C#) owns state and never computes health. The analytics package (Python)
computes health and owns no state. They meet at one document, the verdict, whose example in
`contracts/` is loaded by tests on both sides.

## Consequences
- The gate can be re-tuned, re-run over historical telemetry, or replaced without redeploying the
  thing that holds rollout state.
- The control plane's safety does not depend on the gate being right about *why*: it only acts on
  `status`, and treats anything other than `healthy` as "do not advance".
- The analytics key cannot advance a rollout, so a compromised or buggy gate can stop changes
  but cannot ship them.
- Two languages mean two toolchains in CI. The contract tests are what keep them honest.
