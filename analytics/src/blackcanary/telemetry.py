"""Telemetry records and their reduction to one health row per device."""

from __future__ import annotations

import json
from collections import defaultdict
from collections.abc import Iterable, Iterator
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from statistics import median
from typing import Any, Literal

Cohort = Literal["treated", "control"]


class TelemetryError(ValueError):
    """A telemetry record is malformed."""


@dataclass(frozen=True)
class Sample:
    """One check-in from one device."""

    device_id: str
    cohort: Cohort
    ring: str
    at: datetime
    checkin_ok: bool
    crash_count: int
    boot_seconds: float | None = None

    @classmethod
    def from_json(cls, raw: dict[str, Any]) -> Sample:
        try:
            cohort = raw["cohort"]
            if cohort not in ("treated", "control"):
                raise TelemetryError(f"unknown cohort {cohort!r}")
            boot = raw.get("bootSeconds")
            return cls(
                device_id=str(raw["deviceId"]),
                cohort=cohort,
                ring=str(raw["ring"]),
                at=datetime.fromisoformat(raw["ts"]),
                checkin_ok=bool(raw["checkinOk"]),
                crash_count=int(raw["crashCount"]),
                boot_seconds=None if boot is None else float(boot),
            )
        except KeyError as missing:
            raise TelemetryError(f"missing field {missing}") from missing


@dataclass(frozen=True)
class DeviceHealth:
    """Everything the gate needs to know about one device over the window."""

    device_id: str
    cohort: Cohort
    ring: str
    failed: bool
    boot_seconds: float | None


def read_jsonl(path: Path) -> Iterator[Sample]:
    """Stream samples from a JSON-lines file, reporting the line of any bad record."""
    with path.open(encoding="utf-8") as handle:
        for number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                yield Sample.from_json(json.loads(line))
            except (json.JSONDecodeError, TelemetryError, TypeError, ValueError) as error:
                raise TelemetryError(f"{path}:{number}: {error}") from error


def summarise(samples: Iterable[Sample]) -> list[DeviceHealth]:
    """Collapse check-ins to one row per device.

    The gate compares *devices*, not check-ins: a single crash-looping laptop that
    reports every minute must count once, not drown out the rest of the cohort.
    A device has failed if any check-in failed or reported a crash.
    """
    by_device: dict[str, list[Sample]] = defaultdict(list)
    for sample in samples:
        by_device[sample.device_id].append(sample)

    rows: list[DeviceHealth] = []
    for device_id in sorted(by_device):
        history = sorted(by_device[device_id], key=lambda s: s.at)
        latest = history[-1]
        boots = [s.boot_seconds for s in history if s.boot_seconds is not None]
        rows.append(
            DeviceHealth(
                device_id=device_id,
                cohort=latest.cohort,
                ring=latest.ring,
                failed=any(not s.checkin_ok or s.crash_count > 0 for s in history),
                boot_seconds=median(boots) if boots else None,
            )
        )
    return rows
