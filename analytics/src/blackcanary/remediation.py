"""Guarded remediation: something proposes fixes, policy decides what may run.

The planner is deliberately replaceable — a rule table today, a language model
tomorrow — and therefore deliberately untrusted. Whatever it proposes passes
through the same :class:`Guard`:

* only tools on the allow-list exist, with exactly the arguments they declare;
* the caller's scopes bound what may be touched;
* sensitive tools never run without a named human approver;
* every step is traced, and a failed plan is unwound with each tool's ``undo``.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Literal, Protocol

Disposition = Literal["allowed", "needs_review", "rejected"]
Outcome = Literal["dry_run", "done", "failed", "undone", "skipped"]

ToolFn = Callable[[str, Mapping[str, str]], None]


@dataclass(frozen=True)
class Finding:
    """A drifted setting, as reported by the control plane's drift endpoint."""

    device_id: str
    key: str
    expected: str
    actual: str | None
    severity: str


@dataclass(frozen=True)
class Proposal:
    tool: str
    device_id: str
    args: Mapping[str, str]
    rationale: str


@dataclass(frozen=True)
class Tool:
    name: str
    scope: str
    params: frozenset[str]
    sensitive: bool
    run: ToolFn
    undo: ToolFn | None = None


@dataclass(frozen=True)
class Decision:
    proposal: Proposal
    disposition: Disposition
    reason: str = ""


@dataclass(frozen=True)
class TraceEntry:
    step: int
    tool: str
    device_id: str
    outcome: Outcome
    detail: str = ""


class Planner(Protocol):
    def propose(self, finding: Finding) -> Sequence[Proposal]: ...


@dataclass(frozen=True)
class RulePlanner:
    """Maps a drifted key to a fix by longest matching prefix."""

    rules: Mapping[str, str] = field(
        default_factory=lambda: {
            "disk.encryption": "enable_disk_encryption",
            "firewall.": "apply_setting",
            "screenlock.": "apply_setting",
            "os.autoUpdate": "apply_setting",
            "usb.": "apply_restricted_profile",
        }
    )

    def propose(self, finding: Finding) -> Sequence[Proposal]:
        matches = [prefix for prefix in self.rules if finding.key.startswith(prefix)]
        if not matches:
            return []

        tool = self.rules[max(matches, key=len)]
        args = {"key": finding.key, "value": finding.expected} if tool == "apply_setting" else {}
        found = "not reported" if finding.actual is None else f"'{finding.actual}'"
        return [
            Proposal(
                tool, finding.device_id, args, f"{finding.key} is {found}, expected '{finding.expected}'"
            )
        ]


class Guard:
    """Decides the fate of each proposal. Holds no trust in where it came from."""

    def __init__(self, tools: Sequence[Tool], granted_scopes: frozenset[str]) -> None:
        self._tools = {tool.name: tool for tool in tools}
        self._scopes = granted_scopes

    def tool(self, name: str) -> Tool:
        return self._tools[name]

    def review(self, proposal: Proposal) -> Decision:
        tool = self._tools.get(proposal.tool)
        if tool is None:
            return Decision(proposal, "rejected", f"'{proposal.tool}' is not an allowed tool")
        if tool.scope not in self._scopes:
            return Decision(proposal, "rejected", f"missing scope '{tool.scope}'")
        if set(proposal.args) != tool.params:
            return Decision(proposal, "rejected", f"arguments must be exactly {sorted(tool.params)}")
        if tool.sensitive:
            return Decision(proposal, "needs_review", "sensitive action requires human approval")
        return Decision(proposal, "allowed")


class Executor:
    """Runs reviewed proposals. Dry-run unless told otherwise."""

    def __init__(self, guard: Guard) -> None:
        self._guard = guard

    def run(
        self,
        decisions: Sequence[Decision],
        *,
        approved_by: Mapping[int, str] | None = None,
        dry_run: bool = True,
    ) -> list[TraceEntry]:
        """Execute in order; on the first failure, undo completed steps in reverse.

        ``approved_by`` maps a decision's index to the human who signed it off.
        Steps that are rejected, or sensitive and unapproved, are skipped and traced.
        """
        approvals = approved_by or {}
        trace: list[TraceEntry] = []
        completed: list[tuple[int, Tool, Proposal]] = []

        for step, decision in enumerate(decisions):
            proposal = decision.proposal

            # Re-review at execution time: a decision object is data, and data can be forged.
            current = self._guard.review(proposal)
            if current.disposition == "rejected":
                trace.append(TraceEntry(step, proposal.tool, proposal.device_id, "skipped", current.reason))
                continue
            if current.disposition == "needs_review" and not approvals.get(step):
                trace.append(
                    TraceEntry(step, proposal.tool, proposal.device_id, "skipped", "awaiting human approval")
                )
                continue

            tool = self._guard.tool(proposal.tool)
            approver = approvals.get(step)
            note = f"approved by {approver}" if approver and tool.sensitive else ""

            if dry_run:
                trace.append(TraceEntry(step, tool.name, proposal.device_id, "dry_run", note))
                continue

            try:
                tool.run(proposal.device_id, proposal.args)
            except Exception as error:
                trace.append(TraceEntry(step, tool.name, proposal.device_id, "failed", str(error)))
                trace.extend(self._unwind(completed))
                break

            completed.append((step, tool, proposal))
            trace.append(TraceEntry(step, tool.name, proposal.device_id, "done", note))

        return trace

    @staticmethod
    def _unwind(completed: Sequence[tuple[int, Tool, Proposal]]) -> list[TraceEntry]:
        entries: list[TraceEntry] = []
        for step, tool, proposal in reversed(completed):
            if tool.undo is None:
                entries.append(
                    TraceEntry(step, tool.name, proposal.device_id, "skipped", "no undo available")
                )
                continue
            try:
                tool.undo(proposal.device_id, proposal.args)
                entries.append(TraceEntry(step, tool.name, proposal.device_id, "undone"))
            except Exception as error:
                entries.append(TraceEntry(step, tool.name, proposal.device_id, "failed", f"undo: {error}"))
        return entries


def plan(findings: Sequence[Finding], planner: Planner, guard: Guard) -> list[Decision]:
    """Propose a fix for every finding and put each proposal through the guard."""
    return [guard.review(proposal) for finding in findings for proposal in planner.propose(finding)]
