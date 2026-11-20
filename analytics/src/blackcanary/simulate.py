"""Deterministic synthetic telemetry, for the demo and for tests.

Not a model of any real fleet — just enough structure (a heavy-tailed boot time,
a small background failure rate) to exercise the gate honestly.
"""

from __future__ import annotations

import json
import random
from datetime import UTC, datetime, timedelta

from .telemetry import Cohort, Sample

EPOCH = datetime(2026, 11, 20, 14, 0, tzinfo=UTC)


def cohort(
    *,
    prefix: str,
    cohort: Cohort,
    ring: str,
    devices: int,
    failure_rate: float,
    boot_median: float,
    seed: int,
    checkins: int = 3,
    start: datetime = EPOCH,
) -> list[Sample]:
    """``devices`` machines, each reporting ``checkins`` times ten minutes apart."""
    rng = random.Random(seed)
    samples: list[Sample] = []

    for index in range(devices):
        broken = rng.random() < failure_rate
        base_boot = boot_median * rng.lognormvariate(0, 0.18)
        for tick in range(checkins):
            crashed = broken and tick == checkins - 1
            samples.append(
                Sample(
                    device_id=f"{prefix}-{index:04d}",
                    cohort=cohort,
                    ring=ring,
                    at=start + timedelta(minutes=10 * tick),
                    checkin_ok=not crashed,
                    crash_count=1 if crashed else 0,
                    boot_seconds=round(base_boot * rng.uniform(0.95, 1.05), 2),
                )
            )
    return samples


def to_jsonl(samples: list[Sample]) -> str:
    return "".join(
        json.dumps(
            {
                "deviceId": s.device_id,
                "cohort": s.cohort,
                "ring": s.ring,
                "ts": s.at.isoformat(),
                "checkinOk": s.checkin_ok,
                "crashCount": s.crash_count,
                "bootSeconds": s.boot_seconds,
            }
        )
        + "\n"
        for s in samples
    )
