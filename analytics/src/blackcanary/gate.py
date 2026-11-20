"""Rollout health gate: is the ring that just received a change still healthy?

The gate compares devices that have the change (*treated*) with a holdback that
does not (*control*) over the same window. Comparing against a concurrent control
rather than last week's numbers cancels out everything else that changed —
an OS update, a network outage, Monday morning.

It returns one of three answers. ``inconclusive`` is a first-class result: with too
few devices the honest answer is "wait", and the control plane will not advance on it.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass, field
from datetime import UTC, datetime
from statistics import median
from typing import Any, Literal

from .stats import mann_whitney_greater, two_proportion_p_value
from .telemetry import DeviceHealth

Status = Literal["healthy", "unhealthy", "inconclusive"]


@dataclass(frozen=True)
class GatePolicy:
    #: Below this many devices in either cohort the gate refuses to decide.
    min_devices: int = 20
    #: Absolute ceiling on the treated failure rate, regardless of control.
    max_failure_rate: float = 0.10
    #: Significance level for the treated-vs-control comparisons.
    alpha: float = 0.01
    #: Smallest failure-rate increase worth acting on, so a statistically
    #: significant but operationally trivial difference does not block a rollout.
    min_failure_delta: float = 0.02
    #: Largest tolerated relative increase in median boot time.
    max_boot_regression: float = 0.15

    def __post_init__(self) -> None:
        if self.min_devices < 8:
            raise ValueError("min_devices below 8 invalidates the normal approximations")
        if not 0 < self.alpha < 1:
            raise ValueError("alpha must be in (0, 1)")


@dataclass(frozen=True)
class Verdict:
    """Wire format is pinned by ``contracts/verdict.example.json``."""

    rollout_id: str
    ring: str
    status: Status
    reasons: tuple[str, ...]
    sample_size: int
    evaluated_at: datetime = field(default_factory=lambda: datetime.now(UTC))

    def to_json(self) -> dict[str, Any]:
        return {
            "rolloutId": self.rollout_id,
            "ring": self.ring,
            "status": self.status,
            "reasons": list(self.reasons),
            "sampleSize": self.sample_size,
            "evaluatedAt": self.evaluated_at.isoformat(),
        }

    @classmethod
    def from_json(cls, raw: dict[str, Any]) -> Verdict:
        unknown = set(raw) - {"rolloutId", "ring", "status", "reasons", "sampleSize", "evaluatedAt"}
        if unknown:
            raise ValueError(f"unknown verdict fields: {sorted(unknown)}")
        if raw["status"] not in ("healthy", "unhealthy", "inconclusive"):
            raise ValueError(f"unknown verdict status {raw['status']!r}")
        return cls(
            rollout_id=raw["rolloutId"],
            ring=raw["ring"],
            status=raw["status"],
            reasons=tuple(raw["reasons"]),
            sample_size=int(raw["sampleSize"]),
            evaluated_at=datetime.fromisoformat(raw["evaluatedAt"]),
        )


def evaluate_ring(
    devices: Sequence[DeviceHealth],
    *,
    rollout_id: str,
    ring: str,
    policy: GatePolicy | None = None,
    now: datetime | None = None,
) -> Verdict:
    """Judge one ring of one rollout from per-device health rows."""
    policy = policy or GatePolicy()
    evaluated_at = now or datetime.now(UTC)

    treated = [d for d in devices if d.cohort == "treated" and d.ring == ring]
    control = [d for d in devices if d.cohort == "control"]

    def verdict(status: Status, *reasons: str) -> Verdict:
        return Verdict(rollout_id, ring, status, reasons, len(treated), evaluated_at)

    if len(treated) < policy.min_devices or len(control) < policy.min_devices:
        return verdict(
            "inconclusive",
            f"need {policy.min_devices} devices per cohort; "
            f"have {len(treated)} treated and {len(control)} control",
        )

    problems = [*_failure_problems(treated, control, policy), *_boot_problems(treated, control, policy)]
    return verdict("unhealthy", *problems) if problems else verdict("healthy")


def _p(value: float) -> str:
    return "p<0.0001" if value < 0.0001 else f"p={value:.4f}"


def _failure_problems(
    treated: Sequence[DeviceHealth], control: Sequence[DeviceHealth], policy: GatePolicy
) -> list[str]:
    failed_t = sum(d.failed for d in treated)
    failed_c = sum(d.failed for d in control)
    rate_t = failed_t / len(treated)
    rate_c = failed_c / len(control)

    problems: list[str] = []
    if rate_t > policy.max_failure_rate:
        problems.append(f"failure rate {rate_t:.1%} exceeds the {policy.max_failure_rate:.0%} ceiling")

    p_value = two_proportion_p_value(failed_t, len(treated), failed_c, len(control))
    if p_value < policy.alpha and rate_t - rate_c >= policy.min_failure_delta:
        problems.append(f"failure rate {rate_t:.1%} in treated vs {rate_c:.1%} in control ({_p(p_value)})")
    return problems


def _boot_problems(
    treated: Sequence[DeviceHealth], control: Sequence[DeviceHealth], policy: GatePolicy
) -> list[str]:
    boots_t = [d.boot_seconds for d in treated if d.boot_seconds is not None]
    boots_c = [d.boot_seconds for d in control if d.boot_seconds is not None]
    if len(boots_t) < policy.min_devices or len(boots_c) < policy.min_devices:
        return []  # boot time is optional telemetry; absence is not a failure

    median_t, median_c = median(boots_t), median(boots_c)
    if median_c <= 0:
        return []

    regression = median_t / median_c - 1
    test = mann_whitney_greater(boots_t, boots_c)
    if test.p_value < policy.alpha and regression > policy.max_boot_regression:
        return [
            f"median boot time {median_t:.1f}s vs {median_c:.1f}s in control "
            f"(+{regression:.0%}, {_p(test.p_value)})"
        ]
    return []
