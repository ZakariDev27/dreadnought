"""Find the devices and moments that do not look like the rest of the fleet."""

from __future__ import annotations

import math
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from .stats import ewma, robust_z_scores


@dataclass(frozen=True)
class Outlier:
    device_id: str
    value: float
    score: float


def fleet_outliers(metric_by_device: Mapping[str, float], threshold: float = 3.5) -> list[Outlier]:
    """Devices whose metric sits far from the fleet median, worst first.

    3.5 is the conventional cut-off for modified z-scores. Only the high side is
    reported: for the metrics this is used on (boot time, crash count, check-in
    latency) an unusually low value is not a problem.
    """
    ids = sorted(metric_by_device)
    scores = robust_z_scores([metric_by_device[i] for i in ids])
    found = [
        Outlier(device_id, metric_by_device[device_id], round(score, 2))
        for device_id, score in zip(ids, scores, strict=True)
        if score > threshold
    ]
    return sorted(found, key=lambda o: o.score, reverse=True)


def ewma_breaches(
    series: Sequence[float], *, baseline: int = 10, alpha: float = 0.3, width: float = 3.0
) -> list[int]:
    """Indices where an EWMA control chart leaves its limits.

    The first ``baseline`` points establish the expected mean and spread; later points
    are smoothed and compared with mean ± ``width`` sigma (using the EWMA's own
    asymptotic standard deviation). Smoothing lets the chart catch a slow drift that
    never trips a per-point threshold.
    """
    if baseline < 2:
        raise ValueError("baseline needs at least two points")
    if len(series) <= baseline:
        return []

    reference = series[:baseline]
    mean = sum(reference) / baseline
    sigma = math.sqrt(sum((v - mean) ** 2 for v in reference) / (baseline - 1))
    if sigma == 0:
        return [i for i in range(baseline, len(series)) if series[i] != mean]

    limit = width * sigma * math.sqrt(alpha / (2 - alpha))
    smoothed = ewma([mean, *series[baseline:]], alpha)[1:]
    return [baseline + i for i, value in enumerate(smoothed) if abs(value - mean) > limit]
