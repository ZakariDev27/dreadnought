# 3. Remediation planners are untrusted

## Context
Turning a drift finding into a fix is a good fit for automation, including model-driven planning.
The fixes run on employee machines.

## Decision
A planner only returns `Proposal` values. A separate `Guard` — not the planner — decides whether
each one is allowed, needs human review, or is rejected, based on an allow-list of tools with exact
argument sets and the scopes granted to the caller. The executor re-runs the guard on every step
and defaults to dry-run.

## Consequences
- Swapping the rule table for a model changes what gets proposed, not what can happen.
- A proposal for a tool that does not exist, or with an extra argument, is inert.
- Sensitive tools cannot run without a named approver recorded in the trace.
- Tools must be written with an `undo` where one is possible; where it is not, the unwind says so
  rather than pretending.
